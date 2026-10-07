using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Checkout.PlaceOrder;
using WidgetWorks.Application.Checkout.Reconcile;
using WidgetWorks.Application.Checkout.ReleaseStale;
using WidgetWorks.Application.Pricing;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.Infrastructure.Pricing;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// What happens when the payment provider never says what became of a charge.
///
/// This is the worst-shaped failure in the system, because the tempting answer is wrong. "No answer"
/// looks like "no" — and treating it as a decline releases the stock and tells the customer their
/// payment failed, which, if the money did move, is the one outcome a shop can never take back. So
/// the rule these tests enforce is that an order only ever moves on a definite answer: an
/// unconfirmed charge keeps its reservation, is exempt from the expiry sweep, and waits for
/// reconciliation to find out the truth from the provider's own record.
/// </summary>
public class PaymentReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static FakeTimeProvider Clock() => new(Now);

    private static ShippingAddressInput Address() => new("Jane Doe", "1 Main St", null, "Springfield", "CA", "90001", "US");

    private sealed record Ctx(
        InMemoryCartRepository Carts,
        InMemoryWidgetRepository Widgets,
        InMemoryOrderRepository Orders,
        FakeEmailSender Email,
        Widget Widget,
        Guid CartId);

    private static async Task<Ctx> SetupAsync()
    {
        var widgets = new InMemoryWidgetRepository();
        var widget = new Widget { Id = Guid.NewGuid(), Sku = "WW-1", Name = "Gizmo", IsActive = true, Price = 10m, QuantityOnHand = 10 };
        widgets.Store[widget.Id] = widget;

        var carts = new InMemoryCartRepository();
        var cart = await carts.CreateAsync(null, CancellationToken.None);
        await carts.UpsertItemAsync(cart.Id, widget.Id, 2, default, CancellationToken.None);

        return new Ctx(carts, widgets, new InMemoryOrderRepository(widgets), new FakeEmailSender(), widget, cart.Id);
    }

    private static CheckoutHandler Checkout(Ctx c, IPaymentGateway gateway)
        => new(
            c.Carts,
            c.Widgets,
            c.Orders,
            new OrderPricer(new FlatRateShippingCalculator(), new StateSalesTaxCalculator(new StaticStateTaxRateProvider())),
            gateway,
            c.Email,
            new FakeReconciliationSignal(),
            Clock(),
            NullLogger<CheckoutHandler>.Instance);

    private static ReconcileUnconfirmedPaymentsHandler Reconciler(
        Ctx c,
        IPaymentGateway gateway,
        ILogger<ReconcileUnconfirmedPaymentsHandler>? logger = null,
        DateTimeOffset? at = null)
        => new(
            c.Orders,
            gateway,
            c.Email,
            new RecordingAuditLog(),
            new FakeTimeProvider(at ?? Now),
            new ReconciliationOptions(),
            logger ?? NullLogger<ReconcileUnconfirmedPaymentsHandler>.Instance);

    /// <summary>Places an order whose charge the provider never confirmed.</summary>
    private static async Task<Ctx> GivenAnUnconfirmedChargeAsync()
    {
        var c = await SetupAsync();
        var result = await Checkout(c, new ScriptedGateway(PaymentResult.Indeterminate("Stripe", "no outcome")))
            .Handle(new CheckoutCommand(c.CartId, null, "jane@example.com", Address(), "Standard", "tok_ok"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        return c;
    }

    [Fact]
    public async Task An_unconfirmed_charge_parks_the_order_and_keeps_its_stock()
    {
        var c = await GivenAnUnconfirmedChargeAsync();
        var order = c.Orders.Orders.Single();

        // Not PaymentFailed. The customer is told the order is settling, not that it failed, because
        // we do not know that it did.
        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Equal(Now, order.PaymentUnconfirmedAt);

        // The reservation stands: releasing stock for a charge that may have been taken would let the
        // same units be sold twice.
        Assert.Equal(2, c.Widgets.Store[c.Widget.Id].QuantityReserved);

        // No receipt — there is nothing to confirm yet.
        Assert.Empty(c.Email.Sent);

        // And the cart is gone, as it is for any parked order. Leaving it would invite the shopper to
        // re-submit a basket that may already have been paid for.
        Assert.Null(await c.Carts.GetAsync(c.CartId, CancellationToken.None));
    }

    [Fact]
    public async Task The_expiry_sweep_refuses_to_fail_an_unconfirmed_charge()
    {
        var c = await GivenAnUnconfirmedChargeAsync();

        // Long past any expiry window. An ordinary parked order would be released here.
        var muchLater = new FakeTimeProvider(Now.AddDays(30));
        var released = await new ReleaseStaleReservationsHandler(
            c.Orders,
            muchLater,
            new ReservationOptions { ExpireAfterMinutes = 90 },
            NullLogger<ReleaseStaleReservationsHandler>.Instance).Handle(CancellationToken.None);

        // Nothing. This is the exemption that makes parking safe: without it the sweep would quietly
        // fail a paid order 90 minutes later, which is the original bug with a delay on it.
        Assert.Equal(0, released);
        Assert.Equal(OrderStatus.AwaitingPayment, c.Orders.Orders.Single().Status);
        Assert.Equal(2, c.Widgets.Store[c.Widget.Id].QuantityReserved);
    }

    [Fact]
    public async Task Reconciliation_settles_a_charge_that_had_in_fact_succeeded()
    {
        var c = await GivenAnUnconfirmedChargeAsync();

        var summary = await Reconciler(c, new ScriptedGateway(probe: PaymentResult.Ok("Stripe", "pi_found")))
            .Handle(CancellationToken.None);

        var order = c.Orders.Orders.Single();
        Assert.Equal(new ReconcileUnconfirmedPaymentsHandler.Summary(1, 1, 0, 0), summary);
        Assert.Equal(OrderStatus.Paid, order.Status);

        // The reference we never learned at charge time is recorded now, which is what puts the order
        // back on the ordinary webhook path.
        Assert.Equal("pi_found", order.PaymentReference);

        // Cleared, so the next pass does not pick it up again.
        Assert.Null(order.PaymentUnconfirmedAt);

        // Stock stays committed to an order that was paid for.
        Assert.Equal(2, c.Widgets.Store[c.Widget.Id].QuantityReserved);

        // And the customer finally hears about it — checkout could not send this.
        Assert.Contains(c.Email.Sent, m => m.Subject.Contains("received"));
    }

    [Fact]
    public async Task Reconciliation_releases_the_stock_only_once_the_refusal_is_confirmed()
    {
        var c = await GivenAnUnconfirmedChargeAsync();

        var summary = await Reconciler(c, new ScriptedGateway(probe: PaymentResult.Declined("Stripe", "The payment was not completed.")))
            .Handle(CancellationToken.None);

        var order = c.Orders.Orders.Single();
        Assert.Equal(new ReconcileUnconfirmedPaymentsHandler.Summary(1, 0, 1, 0), summary);
        Assert.Equal(OrderStatus.PaymentFailed, order.Status);
        Assert.Null(order.PaymentUnconfirmedAt);

        // Now — and only now — the units go back on sale.
        Assert.Equal(0, c.Widgets.Store[c.Widget.Id].QuantityReserved);
        Assert.Empty(c.Email.Sent);
    }

    [Fact]
    public async Task A_probe_that_still_cannot_say_changes_nothing_at_all()
    {
        var c = await GivenAnUnconfirmedChargeAsync();

        var summary = await Reconciler(c, new ScriptedGateway(probe: PaymentResult.Indeterminate("Stripe", "No charge found for this order yet.")))
            .Handle(CancellationToken.None);

        var order = c.Orders.Orders.Single();
        Assert.Equal(new ReconcileUnconfirmedPaymentsHandler.Summary(1, 0, 0, 1), summary);

        // Left exactly as found, and still on the list for next time. "Not found" is not proof of
        // absence — a provider's search index lags its own writes, so acting on an empty result would
        // fail orders that had been paid seconds earlier.
        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Equal(Now, order.PaymentUnconfirmedAt);
        Assert.Equal(2, c.Widgets.Store[c.Widget.Id].QuantityReserved);
        Assert.Single(await c.Orders.GetUnconfirmedPaymentsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_charge_still_in_progress_is_handed_back_to_the_webhook_path()
    {
        var c = await GivenAnUnconfirmedChargeAsync();

        var summary = await Reconciler(c, new ScriptedGateway(probe: PaymentResult.Pending("Stripe", "pi_processing")))
            .Handle(CancellationToken.None);

        var order = c.Orders.Orders.Single();
        Assert.Equal(new ReconcileUnconfirmedPaymentsHandler.Summary(1, 0, 0, 0), summary);

        // Still parked, but no longer *unknown*: it has a reference, so the provider's webhook can
        // find it, and clearing the mark returns it to the expiry sweep so it cannot hold stock for
        // ever if that webhook never comes.
        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Equal("pi_processing", order.PaymentReference);
        Assert.Null(order.PaymentUnconfirmedAt);
    }

    [Fact]
    public async Task A_probe_that_throws_leaves_the_order_alone_and_does_not_end_the_pass()
    {
        var c = await GivenAnUnconfirmedChargeAsync();
        var log = new RecordingLogger<ReconcileUnconfirmedPaymentsHandler>();

        var summary = await Reconciler(c, new ThrowingGateway(), log).Handle(CancellationToken.None);

        Assert.Equal(new ReconcileUnconfirmedPaymentsHandler.Summary(1, 0, 0, 1), summary);
        Assert.Equal(OrderStatus.AwaitingPayment, c.Orders.Orders.Single().Status);
        Assert.Equal(2, c.Widgets.Store[c.Widget.Id].QuantityReserved);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("threw"));
    }

    [Fact]
    public async Task An_order_nobody_can_resolve_is_escalated_rather_than_guessed_at()
    {
        var c = await GivenAnUnconfirmedChargeAsync();
        var log = new RecordingLogger<ReconcileUnconfirmedPaymentsHandler>();

        // Well past the escalation window, with the provider still unable to say.
        var summary = await Reconciler(
                c,
                new ScriptedGateway(probe: PaymentResult.Indeterminate("Stripe", "still nothing")),
                log,
                at: Now.AddHours(12))
            .Handle(CancellationToken.None);

        Assert.Equal(1, summary.StillUnknown);

        // An error a human is meant to act on — and nothing else. No status change, no release: an
        // automated guess at this point is how a paid customer gets told they were not.
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("needs a human"));
        Assert.Equal(OrderStatus.AwaitingPayment, c.Orders.Orders.Single().Status);
        Assert.Equal(2, c.Widgets.Store[c.Widget.Id].QuantityReserved);
    }

    [Fact]
    public async Task A_webhook_that_settles_first_is_not_overwritten()
    {
        var c = await GivenAnUnconfirmedChargeAsync();
        var order = c.Orders.Orders.Single();

        // The provider's webhook arrives before the sweep gets there.
        await c.Orders.MarkPaidAsync(order.Id, "Stripe", "pi_from_webhook", Now, CancellationToken.None);

        var summary = await Reconciler(c, new ScriptedGateway(probe: PaymentResult.Ok("Stripe", "pi_from_probe")))
            .Handle(CancellationToken.None);

        // Settling cleared the mark, so there was nothing left to reconcile and the probe was never
        // even consulted — the webhook's reference stands rather than being overwritten by the one the
        // probe would have found.
        Assert.Equal(0, summary.Examined);
        Assert.Equal("pi_from_webhook", order.PaymentReference);

        // No receipt from reconciliation. (The real webhook path sends its own; this test settles the
        // order through the repository directly, so an empty mailbox here is the absence of a
        // *duplicate* receipt, which is the thing worth pinning down.)
        Assert.Empty(c.Email.Sent);
    }

    [Fact]
    public async Task An_empty_queue_costs_nothing()
    {
        var c = await SetupAsync();
        var gateway = new ScriptedGateway(probe: PaymentResult.Ok("Stripe", "pi_never_asked"));

        var summary = await Reconciler(c, gateway).Handle(CancellationToken.None);

        Assert.Equal(new ReconcileUnconfirmedPaymentsHandler.Summary(0, 0, 0, 0), summary);
        Assert.Equal(0, gateway.Probes);   // the provider is not called on a quiet day
    }

    /// <summary>A gateway that answers with whatever the test hands it.</summary>
    private sealed class ScriptedGateway(PaymentResult? charge = null, PaymentResult? probe = null) : IPaymentGateway
    {
        public int Probes { get; private set; }

        public string Name => "Stripe";

        public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct)
            => Task.FromResult(charge ?? PaymentResult.Ok(Name, "pi_charged"));

        public Task<PaymentResult> ProbeAsync(string orderNumber, CancellationToken ct)
        {
            Probes++;
            return Task.FromResult(probe ?? PaymentResult.Indeterminate(Name, "no answer"));
        }

        public Task<PaymentResult> RefundAsync(string reference, decimal amount, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(PaymentResult.Ok(Name, "re_1"));
    }

    private sealed class ThrowingGateway : IPaymentGateway
    {
        public string Name => "Stripe";

        public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct)
            => Task.FromResult(PaymentResult.Indeterminate(Name, "no outcome"));

        public Task<PaymentResult> ProbeAsync(string orderNumber, CancellationToken ct)
            => throw new InvalidOperationException("the provider fell over");

        public Task<PaymentResult> RefundAsync(string reference, decimal amount, string idempotencyKey, CancellationToken ct)
            => throw new InvalidOperationException("the provider fell over");
    }
}
