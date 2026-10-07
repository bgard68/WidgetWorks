using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Orders.Admin;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The staff review list.
///
/// Both halves of it existed only as log lines before, which is the same as not existing: nobody
/// should have to grep to find an order holding stock over an unknown payment, or a customer charged
/// twice. The list is empty on a healthy day — that is the point of it.
/// </summary>
public class PaymentExceptionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Ctx(InMemoryWidgetRepository Widgets, InMemoryOrderRepository Orders, Widget Widget);

    private static Ctx Setup()
    {
        var widgets = new InMemoryWidgetRepository();
        var widget = new Widget { Id = Guid.NewGuid(), Sku = "WW-1", Name = "Gizmo", IsActive = true, Price = 10m, QuantityOnHand = 100 };
        widgets.Store[widget.Id] = widget;
        return new Ctx(widgets, new InMemoryOrderRepository(widgets), widget);
    }

    private static async Task<Order> GivenOrderAsync(
        Ctx c,
        string email = "jane@example.com",
        decimal total = 29.19m,
        DateTimeOffset? at = null,
        string status = OrderStatus.Paid)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = "WW-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Email = email,
            Total = total,
            Status = OrderStatus.Pending,
            CreatedAt = at ?? Now,
            UpdatedAt = at ?? Now,
            Items = [new OrderItem { Id = Guid.NewGuid(), WidgetId = c.Widget.Id, Sku = "WW-1", Name = "Gizmo", UnitPrice = 10m, Quantity = 2, LineSubtotal = 20m }],
        };

        await c.Orders.TryPlaceAsync(order, CancellationToken.None);
        order.Status = status;
        return order;
    }

    private static ListPaymentExceptionsHandler Handler(Ctx c, OrderReviewOptions? options = null)
        => new(c.Orders, options ?? new OrderReviewOptions());

    [Fact]
    public async Task A_healthy_shop_shows_an_empty_list()
    {
        var c = Setup();
        await GivenOrderAsync(c);

        var view = await Handler(c).Handle(CancellationToken.None);

        Assert.Empty(view.Unconfirmed);
        Assert.Empty(view.PossibleDuplicates);
    }

    [Fact]
    public async Task An_order_with_an_unknown_payment_outcome_is_visible_to_staff()
    {
        var c = Setup();
        var order = await GivenOrderAsync(c, status: OrderStatus.Pending);
        await c.Orders.MarkPaymentUnconfirmedAsync(order.Id, "Stripe", Now, CancellationToken.None);

        var view = await Handler(c).Handle(CancellationToken.None);

        var listed = Assert.Single(view.Unconfirmed);
        Assert.Equal(order.OrderNumber, listed.OrderNumber);

        // The timestamp is on the row, so staff can see how long it has been stuck rather than only
        // that it is — which is the difference between a list and a useful one.
        Assert.Equal(Now, listed.PaymentUnconfirmedAt);
        Assert.Equal("jane@example.com", listed.Email);
    }

    [Fact]
    public async Task Two_identical_orders_minutes_apart_are_flagged_for_review()
    {
        var c = Setup();
        var first = await GivenOrderAsync(c, at: Now);
        var second = await GivenOrderAsync(c, at: Now.AddMinutes(2));

        var view = await Handler(c).Handle(CancellationToken.None);

        // Both sides of the pair, because staff need to compare them to decide which to refund.
        Assert.Equal(2, view.PossibleDuplicates.Count);
        Assert.Contains(view.PossibleDuplicates, o => o.OrderNumber == first.OrderNumber);
        Assert.Contains(view.PossibleDuplicates, o => o.OrderNumber == second.OrderNumber);
    }

    [Fact]
    public async Task The_same_order_placed_much_later_is_a_customer_not_a_mistake()
    {
        var c = Setup();
        await GivenOrderAsync(c, at: Now);
        await GivenOrderAsync(c, at: Now.AddHours(3));

        var view = await Handler(c).Handle(CancellationToken.None);

        // Outside the window. Flagging this would be the review queue crying wolf, and a queue that
        // cries wolf is one nobody opens.
        Assert.Empty(view.PossibleDuplicates);
    }

    [Fact]
    public async Task A_retry_after_a_decline_is_not_a_duplicate()
    {
        var c = Setup();
        await GivenOrderAsync(c, at: Now, status: OrderStatus.PaymentFailed);
        await GivenOrderAsync(c, at: Now.AddMinutes(1), status: OrderStatus.Paid);

        var view = await Handler(c).Handle(CancellationToken.None);

        // The shopper's card was refused and they tried again. That is the system working, and nobody
        // was charged twice — so there is nothing for a human to look at.
        Assert.Empty(view.PossibleDuplicates);
    }

    [Fact]
    public async Task Different_customers_or_different_totals_are_never_paired()
    {
        var c = Setup();
        await GivenOrderAsync(c, email: "jane@example.com", total: 29.19m, at: Now);
        await GivenOrderAsync(c, email: "john@example.com", total: 29.19m, at: Now.AddMinutes(1));
        await GivenOrderAsync(c, email: "jane@example.com", total: 10.00m, at: Now.AddMinutes(1));

        var view = await Handler(c).Handle(CancellationToken.None);

        Assert.Empty(view.PossibleDuplicates);
    }

    [Fact]
    public async Task The_window_is_configurable_because_what_counts_as_a_mistake_is_a_judgement()
    {
        var c = Setup();
        await GivenOrderAsync(c, at: Now);
        await GivenOrderAsync(c, at: Now.AddMinutes(30));

        Assert.Empty((await Handler(c).Handle(CancellationToken.None)).PossibleDuplicates);

        var wider = await Handler(c, new OrderReviewOptions { DuplicateWindowMinutes = 60 }).Handle(CancellationToken.None);
        Assert.Equal(2, wider.PossibleDuplicates.Count);
    }

    [Fact]
    public async Task An_unconfirmed_charge_nudges_the_reconciler_instead_of_waiting_for_a_sweep()
    {
        // The signal is the difference between an order holding stock for a minute and holding it for
        // an hour, so it is worth asserting that checkout actually rings it.
        var signal = new FakeReconciliationSignal();
        var c = Setup();

        var widgets = c.Widgets;
        var carts = new InMemoryCartRepository();
        var cart = await carts.CreateAsync(null, CancellationToken.None);
        await carts.UpsertItemAsync(cart.Id, c.Widget.Id, 2, default, CancellationToken.None);

        var handler = new Application.Checkout.PlaceOrder.CheckoutHandler(
            carts,
            widgets,
            c.Orders,
            new Application.Pricing.OrderPricer(
                new Infrastructure.Pricing.FlatRateShippingCalculator(),
                new Infrastructure.Pricing.StateSalesTaxCalculator(new Infrastructure.Pricing.StaticStateTaxRateProvider())),
            new UnconfirmingGateway(),
            new FakeEmailSender(),
            signal,
            new FakeTimeProvider(Now),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Application.Checkout.PlaceOrder.CheckoutHandler>.Instance);

        var result = await handler.Handle(
            new Application.Checkout.PlaceOrder.CheckoutCommand(
                cart.Id, null, "jane@example.com",
                new Application.Checkout.PlaceOrder.ShippingAddressInput("Jane Doe", "1 Main St", null, "Springfield", "CA", "90001", "US"),
                "Standard", "tok_ok"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, signal.Notifications);
    }

    private sealed class UnconfirmingGateway : Application.Abstractions.IPaymentGateway
    {
        public string Name => "Stripe";

        public Task<Application.Abstractions.PaymentResult> ChargeAsync(Application.Abstractions.PaymentRequest request, CancellationToken ct)
            => Task.FromResult(Application.Abstractions.PaymentResult.Indeterminate(Name, "no outcome"));

        public Task<Application.Abstractions.PaymentResult> ProbeAsync(string orderNumber, CancellationToken ct)
            => Task.FromResult(Application.Abstractions.PaymentResult.Indeterminate(Name, "no answer"));

        public Task<Application.Abstractions.PaymentResult> RefundAsync(string reference, decimal amount, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(Application.Abstractions.PaymentResult.Declined(Name, "not refundable"));
    }
}
