using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Checkout.ReleaseStale;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The sweep that stops an unsettled order holding stock forever. Time is faked, so these prove the
/// policy — what counts as stale, what gets released — without a timer or a real wait.
/// </summary>
public class ReleaseStaleReservationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Harness(
        InMemoryOrderRepository Orders,
        InMemoryWidgetRepository Widgets,
        ReleaseStaleReservationsHandler Handler,
        Guid WidgetId);

    private static Harness Build(ReservationOptions? options = null)
    {
        var widgets = new InMemoryWidgetRepository();
        var orders = new InMemoryOrderRepository(widgets);
        var widgetId = Guid.NewGuid();
        widgets.Store[widgetId] = new Widget
        {
            Id = widgetId,
            Sku = "WW-001",
            Name = "Standard Widget Block Cobalt",
            Price = 9.99m,
            IsActive = true,
            QuantityOnHand = 20,
            QuantityReserved = 0,
        };

        var handler = new ReleaseStaleReservationsHandler(
            orders,
            new FakeTimeProvider(Now),
            options ?? new ReservationOptions(),
            NullLogger<ReleaseStaleReservationsHandler>.Instance);

        return new Harness(orders, widgets, handler, widgetId);
    }

    /// <summary>Places an order holding stock and parks it in AwaitingPayment as of <paramref name="updatedAt"/>.</summary>
    private static async Task<Order> GivenUnsettledOrder(Harness h, int quantity, DateTimeOffset updatedAt)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = "WW-" + Guid.NewGuid().ToString("N")[..6],
            Email = "shopper@widgetworks.test",
            Status = OrderStatus.Pending,
            Total = 9.99m * quantity,
        };
        order.Items.Add(new OrderItem
        {
            Id = Guid.NewGuid(),
            WidgetId = h.WidgetId,
            Sku = "WW-001",
            Name = "Standard Widget Block Cobalt",
            UnitPrice = 9.99m,
            Quantity = quantity,
            LineSubtotal = 9.99m * quantity,
        });

        await h.Orders.TryPlaceAsync(order, CancellationToken.None);
        await h.Orders.MarkAwaitingPaymentAsync(order.Id, "Mock", "ref", updatedAt, CancellationToken.None);
        return order;
    }

    [Fact]
    public async Task An_order_unsettled_past_the_threshold_gives_its_stock_back()
    {
        var h = Build();
        await GivenUnsettledOrder(h, quantity: 4, updatedAt: Now.AddMinutes(-30));

        Assert.Equal(4, h.Widgets.Store[h.WidgetId].QuantityReserved);

        var released = await h.Handler.Handle(CancellationToken.None);

        Assert.Equal(1, released);
        Assert.Equal(0, h.Widgets.Store[h.WidgetId].QuantityReserved);
        // The goods never shipped, so on-hand is untouched and all 20 are sellable again.
        Assert.Equal(20, h.Widgets.Store[h.WidgetId].QuantityOnHand);
    }

    [Fact]
    public async Task An_order_still_inside_the_window_is_left_alone()
    {
        var h = Build();
        await GivenUnsettledOrder(h, quantity: 4, updatedAt: Now.AddMinutes(-5));

        var released = await h.Handler.Handle(CancellationToken.None);

        // A slow but honest bank redirect must not lose the customer's basket.
        Assert.Equal(0, released);
        Assert.Equal(4, h.Widgets.Store[h.WidgetId].QuantityReserved);
    }

    [Fact]
    public async Task A_settled_order_is_never_swept()
    {
        var h = Build();
        var order = await GivenUnsettledOrder(h, quantity: 4, updatedAt: Now.AddMinutes(-30));
        await h.Orders.MarkPaidAsync(order.Id, "Mock", "ref", Now.AddMinutes(-29), CancellationToken.None);

        var released = await h.Handler.Handle(CancellationToken.None);

        Assert.Equal(0, released);
        // Paid stock is owed to the customer and must stay reserved until it ships.
        Assert.Equal(4, h.Widgets.Store[h.WidgetId].QuantityReserved);
    }

    [Fact]
    public async Task A_sweep_is_safe_to_run_twice()
    {
        var h = Build();
        await GivenUnsettledOrder(h, quantity: 4, updatedAt: Now.AddMinutes(-30));

        var first = await h.Handler.Handle(CancellationToken.None);
        var second = await h.Handler.Handle(CancellationToken.None);

        Assert.Equal(1, first);
        // Nothing left to do, and crucially no second decrement of a reservation already released.
        Assert.Equal(0, second);
        Assert.Equal(0, h.Widgets.Store[h.WidgetId].QuantityReserved);
    }

    [Fact]
    public async Task One_pass_takes_no_more_than_the_batch_size()
    {
        var h = Build(new ReservationOptions { BatchSize = 2 });
        for (var i = 0; i < 5; i++)
        {
            await GivenUnsettledOrder(h, quantity: 1, updatedAt: Now.AddMinutes(-30 - i));
        }

        var released = await h.Handler.Handle(CancellationToken.None);

        // A backlog is worked through over several sweeps rather than one long pass.
        Assert.Equal(2, released);
        Assert.Equal(3, h.Widgets.Store[h.WidgetId].QuantityReserved);
    }

    [Fact]
    public async Task The_oldest_unsettled_orders_are_released_first()
    {
        var h = Build(new ReservationOptions { BatchSize = 1 });
        var oldest = await GivenUnsettledOrder(h, quantity: 1, updatedAt: Now.AddHours(-3));
        await GivenUnsettledOrder(h, quantity: 1, updatedAt: Now.AddMinutes(-20));

        await h.Handler.Handle(CancellationToken.None);

        Assert.Equal(OrderStatus.PaymentFailed, h.Orders.Orders.Single(o => o.Id == oldest.Id).Status);
    }

    [Fact]
    public async Task A_cancelled_sweep_stops_rather_than_finishing_the_batch()
    {
        var h = Build();
        await GivenUnsettledOrder(h, quantity: 1, updatedAt: Now.AddMinutes(-30));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Handler.Handle(cancelled.Token));
    }
}
