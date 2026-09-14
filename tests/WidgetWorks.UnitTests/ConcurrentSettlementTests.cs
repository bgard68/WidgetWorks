using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Catalog.Inventory;
using WidgetWorks.Application.Checkout.ConfirmPayment;
using WidgetWorks.Application.Checkout.ReleaseStale;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The callers that lose a compare-and-set.
///
/// Every write that settles an order or moves stock is a conditional UPDATE, and each caller is
/// expected to check whether it won. The winner's path is covered by the suites next to it; this one
/// covers the loser, which is where the money is: a second receipt, a reservation handed back under
/// an order that was actually paid, a write-off that overwrites a sale taken in the same instant.
///
/// The loss is produced rather than stubbed. A competing write goes through the same store in the gap
/// between the caller's read and its write, so the compare-and-set declines for the reason PostgreSQL
/// would decline it — a fake hard-coded to return false would also pass against a repository that had
/// no compare-and-set at all.
/// </summary>
public class ConcurrentSettlementTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Harness(InMemoryWidgetRepository Widgets, InMemoryOrderRepository Orders, Guid WidgetId);

    private static Harness NewHarness()
    {
        var widgets = new InMemoryWidgetRepository();
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

        return new Harness(widgets, new InMemoryOrderRepository(widgets), widgetId);
    }

    /// <summary>Places an order that holds stock and parks it in AwaitingPayment as of <paramref name="updatedAt"/>.</summary>
    private static async Task<Order> GivenOrderAwaitingPayment(Harness h, int quantity, string reference, DateTimeOffset updatedAt)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = "WW-" + reference,
            Email = "shopper@widgetworks.test",
            Status = OrderStatus.Pending,
            Total = 9.99m * quantity,
        };
        order.Items.Add(new OrderItem
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            WidgetId = h.WidgetId,
            Sku = "WW-001",
            Name = "Standard Widget Block Cobalt",
            UnitPrice = 9.99m,
            Quantity = quantity,
            LineSubtotal = 9.99m * quantity,
        });

        await h.Orders.TryPlaceAsync(order, CancellationToken.None);
        await h.Orders.MarkAwaitingPaymentAsync(order.Id, "Mock", reference, updatedAt, CancellationToken.None);
        return order;
    }

    [Fact]
    public async Task ConfirmPayment_SucceededEventLosesTheSettlementRace_ReportsPaidAndSendsNoSecondReceipt()
    {
        // Arrange — two deliveries of the same webhook are in flight. Both read AwaitingPayment; the
        // other one wins the UPDATE while this one is still between its read and its write.
        var h = NewHarness();
        var order = await GivenOrderAwaitingPayment(h, quantity: 3, reference: "pi_race_ok", updatedAt: Now);
        var email = new FakeEmailSender();
        var raced = new RacedOrderRepository(
            h.Orders,
            (inner, o) => inner.MarkPaidAsync(o.Id, "Mock", "pi_race_ok", Now, CancellationToken.None));
        var handler = new ConfirmPaymentHandler(raced, email, new FakeTimeProvider(Now), NullLogger<ConfirmPaymentHandler>.Instance);

        // Act
        var result = await handler.Handle(
            new ConfirmPaymentCommand("Mock", PaymentEventType.Succeeded, "pi_race_ok"), CancellationToken.None);

        // Assert — the loser reports the winner's outcome and emails nothing, so the customer is
        // charged once and thanked once.
        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Paid, result.Value);
        Assert.Equal(OrderStatus.Paid, h.Orders.Orders.Single(o => o.Id == order.Id).Status);
        Assert.Empty(email.Sent);
        Assert.Equal(3, h.Widgets.Store[h.WidgetId].QuantityReserved);
    }

    [Fact]
    public async Task ConfirmPayment_FailedEventLosesToASuccessInFlight_LeavesTheOrderPaidAndTheStockCommitted()
    {
        // Arrange — a Failed and a Succeeded event race. Both read AwaitingPayment, so the status
        // check at the top of the handler lets both through; the success wins the UPDATE.
        var h = NewHarness();
        var order = await GivenOrderAwaitingPayment(h, quantity: 4, reference: "pi_race_fail", updatedAt: Now);
        var raced = new RacedOrderRepository(
            h.Orders,
            (inner, o) => inner.MarkPaidAsync(o.Id, "Mock", "pi_race_fail", Now, CancellationToken.None));
        var handler = new ConfirmPaymentHandler(
            raced, new FakeEmailSender(), new FakeTimeProvider(Now), NullLogger<ConfirmPaymentHandler>.Instance);

        // Act
        var result = await handler.Handle(
            new ConfirmPaymentCommand("Mock", PaymentEventType.Failed, "pi_race_fail"), CancellationToken.None);

        // Assert — the failure event must not overwrite a settled order, and must not hand back the
        // four units that a paying customer is now owed.
        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Paid, result.Value);
        Assert.Equal(OrderStatus.Paid, h.Orders.Orders.Single(o => o.Id == order.Id).Status);
        Assert.Equal(4, h.Widgets.Store[h.WidgetId].QuantityReserved);
        Assert.Equal(20, h.Widgets.Store[h.WidgetId].QuantityOnHand);
    }

    [Fact]
    public async Task ReleaseStaleReservations_AWebhookSettlesAnOrderMidSweep_CountsOnlyWhatItActuallyReleased()
    {
        // Arrange — two orders old enough to sweep. A late webhook settles the first in the gap
        // between the sweep's query and its write; the same webhook then loses harmlessly on the second.
        var h = NewHarness();
        var settled = await GivenOrderAwaitingPayment(h, quantity: 2, reference: "pi_settled", updatedAt: Now.AddMinutes(-40));
        var abandoned = await GivenOrderAwaitingPayment(h, quantity: 3, reference: "pi_abandoned", updatedAt: Now.AddMinutes(-30));
        var raced = new RacedOrderRepository(
            h.Orders,
            (inner, _) => inner.MarkPaidAsync(settled.Id, "Mock", "pi_settled", Now, CancellationToken.None));
        var handler = new ReleaseStaleReservationsHandler(
            // Explicit 15-minute window so the ~30/40-min-old orders above stay sweepable
            // regardless of the production default (tuned longer for Neon cost).
            raced, new FakeTimeProvider(Now), new ReservationOptions { ExpireAfterMinutes = 15, SweepIntervalMinutes = 5 }, NullLogger<ReleaseStaleReservationsHandler>.Instance);

        Assert.Equal(5, h.Widgets.Store[h.WidgetId].QuantityReserved);

        // Act
        var released = await handler.Handle(CancellationToken.None);

        // Assert — the sweep carries on past the order it lost, counts only the one it released, and
        // leaves the paid order's two units committed.
        Assert.Equal(1, released);
        Assert.Equal(OrderStatus.Paid, h.Orders.Orders.Single(o => o.Id == settled.Id).Status);
        Assert.Equal(OrderStatus.PaymentFailed, h.Orders.Orders.Single(o => o.Id == abandoned.Id).Status);
        Assert.Equal(2, h.Widgets.Store[h.WidgetId].QuantityReserved);
        Assert.Equal(20, h.Widgets.Store[h.WidgetId].QuantityOnHand);
    }

    [Fact]
    public async Task AdjustInventory_StockIsReservedBetweenTheReadAndTheWrite_RefusesAndChangesNothing()
    {
        // Arrange — ten on hand and none reserved, so the handler's advisory checks see plenty of
        // room for an eight-unit write-off. A checkout reserves five before the UPDATE lands.
        var widgets = new InMemoryWidgetRepository();
        var widgetId = Guid.NewGuid();
        widgets.Store[widgetId] = new Widget
        {
            Id = widgetId,
            Sku = "WW-002",
            Name = "Reinforced Widget Frame",
            Price = 24.50m,
            IsActive = true,
            QuantityOnHand = 10,
            QuantityReserved = 0,
        };
        var raced = new ReservedInTheGapWidgetRepository(widgets, reservedInTheGap: 5);
        var handler = new AdjustInventoryHandler(raced, new FakeTimeProvider(Now));

        // Act
        var result = await handler.Handle(new AdjustInventoryCommand(widgetId, -8), CancellationToken.None);

        // Assert — refused whole, and named for the reason the UPDATE refused it rather than the
        // reason the stale read would have given.
        Assert.True(result.IsFailure);
        Assert.Contains("reserved quantity", result.Error, StringComparison.Ordinal);
        Assert.Equal(10, widgets.Store[widgetId].QuantityOnHand);
        Assert.Equal(5, widgets.Store[widgetId].QuantityReserved);
    }

    /// <summary>
    /// <see cref="InMemoryOrderRepository"/> with a competing writer that lands in the gap between a
    /// caller's read and its write — the window each Mark* compare-and-set exists to close.
    ///
    /// The competitor writes through the same store, so the caller's own call is declined by the
    /// repository's real rule. Everything else is forwarded untouched.
    /// </summary>
    private sealed class RacedOrderRepository(
        InMemoryOrderRepository inner,
        Func<InMemoryOrderRepository, Order, Task> competitor) : IOrderRepository
    {
        public async Task<bool> MarkPaidAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct)
        {
            await competitor(inner, inner.Orders.Single(o => o.Id == orderId));
            return await inner.MarkPaidAsync(orderId, provider, reference, now, ct);
        }

        public async Task<bool> MarkPaymentFailedAsync(Order order, string reason, DateTimeOffset now, CancellationToken ct)
        {
            await competitor(inner, order);
            return await inner.MarkPaymentFailedAsync(order, reason, now, ct);
        }

        public Task<bool> TryPlaceAsync(Order order, CancellationToken ct)
            => inner.TryPlaceAsync(order, ct);

        public Task<bool> MarkAwaitingPaymentAsync(Guid orderId, string provider, string reference, DateTimeOffset now, CancellationToken ct)
            => inner.MarkAwaitingPaymentAsync(orderId, provider, reference, now, ct);

        public Task UpdateStatusAsync(Order order, DateTimeOffset now, CancellationToken ct)
            => inner.UpdateStatusAsync(order, now, ct);

        public Task<IReadOnlyList<Order>> GetStaleAwaitingPaymentAsync(DateTimeOffset cutoff, int limit, CancellationToken ct)
            => inner.GetStaleAwaitingPaymentAsync(cutoff, limit, ct);

        public Task<Order?> GetByIdAsync(Guid id, CancellationToken ct)
            => inner.GetByIdAsync(id, ct);

        public Task<Order?> GetByPaymentReferenceAsync(string provider, string reference, CancellationToken ct)
            => inner.GetByPaymentReferenceAsync(provider, reference, ct);

        public Task<Order?> GetByNumberAndEmailAsync(string orderNumber, string email, CancellationToken ct)
            => inner.GetByNumberAndEmailAsync(orderNumber, email, ct);

        public Task<IReadOnlyList<Order>> GetForUserAsync(Guid userId, CancellationToken ct)
            => inner.GetForUserAsync(userId, ct);

        public Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken ct)
            => inner.GetRecentAsync(limit, ct);
    }

    /// <summary>
    /// <see cref="InMemoryWidgetRepository"/> where a checkout reserves stock in the gap between the
    /// inventory handler's read and its write. The reservation is real, so the refusal comes from the
    /// repository's own on-hand-versus-reserved rule rather than from a stubbed return.
    /// </summary>
    private sealed class ReservedInTheGapWidgetRepository(InMemoryWidgetRepository inner, int reservedInTheGap) : IWidgetRepository
    {
        public Task<int?> AdjustStockAsync(Guid id, int delta, DateTimeOffset now, CancellationToken ct)
        {
            inner.Store[id].QuantityReserved += reservedInTheGap;
            return inner.AdjustStockAsync(id, delta, now, ct);
        }

        public Task<Widget?> GetByIdAsync(Guid id, CancellationToken ct)
            => inner.GetByIdAsync(id, ct);

        public Task<Widget?> GetBySkuAsync(string normalizedSku, CancellationToken ct)
            => inner.GetBySkuAsync(normalizedSku, ct);

        public Task<IReadOnlyList<Widget>> SearchAsync(WidgetQuery query, CancellationToken ct)
            => inner.SearchAsync(query, ct);

        public Task<int> CountAsync(WidgetQuery query, CancellationToken ct)
            => inner.CountAsync(query, ct);

        public Task AddAsync(Widget widget, CancellationToken ct)
            => inner.AddAsync(widget, ct);

        public Task UpdateDetailsAsync(Widget widget, CancellationToken ct)
            => inner.UpdateDetailsAsync(widget, ct);

        public Task ArchiveAsync(Guid id, DateTimeOffset archivedAt, CancellationToken ct)
            => inner.ArchiveAsync(id, archivedAt, ct);

        public Task<int> CountOrderLinesAsync(Guid widgetId, CancellationToken ct)
            => inner.CountOrderLinesAsync(widgetId, ct);

        public Task DeleteAsync(Guid id, CancellationToken ct)
            => inner.DeleteAsync(id, ct);
    }
}
