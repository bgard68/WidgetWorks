using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Orders.Refund;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// Giving money back.
///
/// Three things decide whether this is safe, and all three are about ordering: the provider is asked
/// before the order is written, so an order never claims a refund that did not happen; the key is
/// derived from the order, so a retried refund pays out once; and an unconfirmed refund is not
/// reported as a failure, because telling staff it failed is how a customer gets paid twice.
/// </summary>
public class RefundOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Ctx(InMemoryWidgetRepository Widgets, InMemoryOrderRepository Orders, Order Order, Widget Widget);

    private static async Task<Ctx> GivenAPaidOrderAsync(string status = OrderStatus.Paid)
    {
        var widgets = new InMemoryWidgetRepository();
        var widget = new Widget { Id = Guid.NewGuid(), Sku = "WW-1", Name = "Gizmo", IsActive = true, Price = 10m, QuantityOnHand = 10 };
        widgets.Store[widget.Id] = widget;

        var orders = new InMemoryOrderRepository(widgets);
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = "WW-20260101-AAA111",
            Email = "jane@example.com",
            Total = 29.19m,
            Status = OrderStatus.Pending,
            CreatedAt = Now,
            UpdatedAt = Now,
            Items = [new OrderItem { Id = Guid.NewGuid(), WidgetId = widget.Id, Sku = widget.Sku, Name = widget.Name, UnitPrice = 10m, Quantity = 2, LineSubtotal = 20m }],
        };

        await orders.TryPlaceAsync(order, CancellationToken.None);
        if (status == OrderStatus.Paid)
        {
            await orders.MarkPaidAsync(order.Id, "Mock", "pi_charged", Now, CancellationToken.None);
        }
        else
        {
            order.Status = status;
        }

        return new Ctx(widgets, orders, order, widget);
    }

    private static RefundOrderHandler Handler(
        Ctx c,
        RecordingGateway gateway,
        ILogger<RefundOrderHandler>? logger = null,
        RecordingAuditLog? audit = null)
        => new(
            c.Orders,
            gateway,
            audit ?? new RecordingAuditLog(),
            new FakeTimeProvider(Now),
            logger ?? NullLogger<RefundOrderHandler>.Instance);

    [Fact]
    public async Task A_refund_returns_the_money_and_puts_the_stock_back_on_sale()
    {
        var c = await GivenAPaidOrderAsync();
        var gateway = new RecordingGateway();

        var result = await Handler(c, gateway).Handle(new RefundOrderCommand(c.Order.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Refunded, result.Value!.Status);

        // The charge's own reference is what gets refunded, for the full order total.
        Assert.Equal("pi_charged", gateway.LastReference);
        Assert.Equal(29.19m, gateway.LastAmount);

        // Nothing had shipped, so the hold is handed back and the units are sellable again.
        Assert.Equal(0, c.Widgets.Store[c.Widget.Id].QuantityReserved);
        Assert.Equal(10, c.Widgets.Store[c.Widget.Id].QuantityOnHand);
    }

    [Fact]
    public async Task Refunding_twice_pays_out_once()
    {
        var c = await GivenAPaidOrderAsync();
        var gateway = new RecordingGateway();
        var handler = Handler(c, gateway);

        var first = await handler.Handle(new RefundOrderCommand(c.Order.Id), CancellationToken.None);
        var second = await handler.Handle(new RefundOrderCommand(c.Order.Id), CancellationToken.None);

        Assert.True(first.IsSuccess);

        // The second click is a no-op rather than a second payout: the order is already Refunded, so
        // the provider is never asked again.
        Assert.True(second.IsSuccess);
        Assert.Equal(OrderStatus.Refunded, second.Value!.Status);
        Assert.Equal(1, gateway.Calls);
        Assert.Equal(0, c.Widgets.Store[c.Widget.Id].QuantityReserved);   // released once, not twice
    }

    [Fact]
    public async Task Every_attempt_at_one_refund_carries_the_same_key()
    {
        var c = await GivenAPaidOrderAsync();

        // The provider refuses, so the order stays Paid and staff can try again — which is exactly the
        // case where a fresh key each time would eventually pay out twice.
        var gateway = new RecordingGateway(PaymentResult.Declined("Mock", "temporarily unavailable"));
        var handler = Handler(c, gateway);

        await handler.Handle(new RefundOrderCommand(c.Order.Id), CancellationToken.None);
        await handler.Handle(new RefundOrderCommand(c.Order.Id), CancellationToken.None);

        Assert.Equal(2, gateway.Calls);
        Assert.Single(gateway.Keys.Distinct());

        // The key names the order and the running total it would reach, so retrying this refund is the
        // same key — while a genuinely different partial refund later is legitimately a different one.
        Assert.Equal("refund-WW-20260101-AAA111-29.19", gateway.Keys[0]);
    }

    [Fact]
    public async Task A_refund_the_provider_cannot_confirm_leaves_the_order_exactly_as_it_was()
    {
        var c = await GivenAPaidOrderAsync();
        var log = new RecordingLogger<RefundOrderHandler>();
        var gateway = new RecordingGateway(PaymentResult.Indeterminate("Mock", "The refund could not be confirmed."));

        var result = await Handler(c, gateway, log).Handle(new RefundOrderCommand(c.Order.Id), CancellationToken.None);

        // Reported as a failure to the caller — but the order is untouched, which is the important
        // half. Marking it Refunded over a refund that may not exist would strand the customer's money;
        // releasing the stock would compound it.
        Assert.True(result.IsFailure);
        Assert.Equal(OrderStatus.Paid, c.Orders.Orders.Single().Status);
        Assert.Equal(2, c.Widgets.Store[c.Widget.Id].QuantityReserved);

        // And it is escalated, because only a human can find out what really happened.
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("could not be confirmed"));
    }

    [Fact]
    public async Task A_refund_the_provider_refuses_leaves_the_order_paid()
    {
        var c = await GivenAPaidOrderAsync();
        var gateway = new RecordingGateway(PaymentResult.Declined("Mock", "Charge already refunded."));

        var result = await Handler(c, gateway).Handle(new RefundOrderCommand(c.Order.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Charge already refunded.", result.Error);
        Assert.Equal(OrderStatus.Paid, c.Orders.Orders.Single().Status);
        Assert.Equal(2, c.Widgets.Store[c.Widget.Id].QuantityReserved);
    }

    [Theory]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.AwaitingPayment)]
    [InlineData(OrderStatus.PaymentFailed)]
    public async Task Only_a_paid_order_can_be_refunded_here(string status)
    {
        var c = await GivenAPaidOrderAsync(status);
        var gateway = new RecordingGateway();

        var result = await Handler(c, gateway).Handle(new RefundOrderCommand(c.Order.Id), CancellationToken.None);

        // A shipped order's money is only half the question — the goods are in transit — and that is a
        // returns workflow. Refusing beats pretending, and the provider is never even called.
        Assert.True(result.IsFailure);
        Assert.Contains("Only a paid order can be refunded", result.Error);
        Assert.Equal(0, gateway.Calls);
    }

    [Fact]
    public async Task An_order_that_does_not_exist_is_not_found()
    {
        var c = await GivenAPaidOrderAsync();
        var gateway = new RecordingGateway();

        var result = await Handler(c, gateway).Handle(new RefundOrderCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Order not found.", result.Error);
        Assert.Equal(0, gateway.Calls);
    }

    [Fact]
    public void A_refund_cannot_be_reached_through_the_generic_status_endpoint()
    {
        // Refunded is deliberately absent from the transition table: that endpoint cannot issue a
        // refund, so it must not be able to claim one happened. The refund route is the only way in.
        Assert.DoesNotContain(OrderStatus.Refunded, OrderStatus.AllowedNext(OrderStatus.Paid));
        Assert.False(OrderStatus.CanTransition(OrderStatus.Paid, OrderStatus.Refunded));
    }

    private sealed class RecordingGateway(PaymentResult? refund = null, Action? onCharge = null) : IPaymentGateway
    {
        public int Calls { get; private set; }

        public List<string> Keys { get; } = [];

        public string? LastReference { get; private set; }

        public decimal LastAmount { get; private set; }

        public string Name => "Mock";

        public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct)
            => Task.FromResult(PaymentResult.Ok(Name, "pi_charged"));

        public Task<PaymentResult> ProbeAsync(string orderNumber, CancellationToken ct)
            => Task.FromResult(PaymentResult.Indeterminate(Name, "not probed"));

        public Task<PaymentResult> RefundAsync(string reference, decimal amount, string idempotencyKey, CancellationToken ct)
        {
            Calls++;
            Keys.Add(idempotencyKey);
            LastReference = reference;
            LastAmount = amount;

            // Lets a test slip another refund in between the provider paying out and the order being
            // written, which is the race the compare-and-set exists for.
            onCharge?.Invoke();

            return Task.FromResult(refund ?? PaymentResult.Ok(Name, "re_1"));
        }
    }

    [Fact]
    public async Task A_partial_refund_leaves_the_order_paid_and_the_goods_owed()
    {
        var c = await GivenAPaidOrderAsync();
        var gateway = new RecordingGateway();

        var result = await Handler(c, gateway).Handle(new RefundOrderCommand(c.Order.Id, Amount: 10m), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(10m, gateway.LastAmount);

        // Still Paid, because the customer is still owed goods for the balance.
        Assert.Equal(OrderStatus.Paid, result.Value!.Status);
        Assert.Equal(10m, result.Value!.RefundedTotal);

        // And the stock stays committed for exactly that reason.
        Assert.Equal(2, c.Widgets.Store[c.Widget.Id].QuantityReserved);
    }

    [Fact]
    public async Task Partial_refunds_accumulate_and_the_last_one_settles_the_order()
    {
        var c = await GivenAPaidOrderAsync();
        var gateway = new RecordingGateway();
        var handler = Handler(c, gateway);

        await handler.Handle(new RefundOrderCommand(c.Order.Id, Amount: 10m), CancellationToken.None);
        await handler.Handle(new RefundOrderCommand(c.Order.Id, Amount: 9m), CancellationToken.None);
        var last = await handler.Handle(new RefundOrderCommand(c.Order.Id, Amount: 10.19m), CancellationToken.None);

        Assert.Equal(OrderStatus.Refunded, last.Value!.Status);
        Assert.Equal(29.19m, last.Value!.RefundedTotal);

        // Three different keys, because these are three different payouts — and the stock comes back
        // only now that nothing is owed.
        Assert.Equal(3, gateway.Keys.Distinct().Count());
        Assert.Equal(0, c.Widgets.Store[c.Widget.Id].QuantityReserved);
    }

    [Fact]
    public async Task A_refund_cannot_exceed_what_the_order_still_owes()
    {
        var c = await GivenAPaidOrderAsync();
        var gateway = new RecordingGateway();
        var handler = Handler(c, gateway);

        await handler.Handle(new RefundOrderCommand(c.Order.Id, Amount: 20m), CancellationToken.None);
        var tooMuch = await handler.Handle(new RefundOrderCommand(c.Order.Id, Amount: 20m), CancellationToken.None);

        // Refused before the provider is asked: paying out more than was charged is not something to
        // discover from a constraint violation.
        Assert.True(tooMuch.IsFailure);
        Assert.Contains("9.19 of this order remains refundable", tooMuch.Error);
        Assert.Equal(1, gateway.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_refund_of_nothing_or_less_is_refused(decimal amount)
    {
        var c = await GivenAPaidOrderAsync();
        var gateway = new RecordingGateway();

        var result = await Handler(c, gateway).Handle(new RefundOrderCommand(c.Order.Id, Amount: amount), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Refund amount must be positive.", result.Error);
        Assert.Equal(0, gateway.Calls);
    }

    [Fact]
    public async Task A_refund_records_who_did_it_and_how_much()
    {
        var c = await GivenAPaidOrderAsync();
        var audit = new RecordingAuditLog();
        var actor = Guid.NewGuid();

        await Handler(c, new RecordingGateway(), audit: audit)
            .Handle(new RefundOrderCommand(c.Order.Id, ActorId: actor), CancellationToken.None);

        // Money moved at a staff member's request, so the trail has to be able to answer who and how
        // much. A log line is not a trail — nobody queries those a month later.
        Assert.Contains("order.refunded", audit.Actions);
        Assert.Contains(audit.Entries, e => e.UserId == actor && e.Detail!.Contains("29.19"));
    }

    [Fact]
    public async Task A_partial_refund_is_recorded_as_partial()
    {
        var c = await GivenAPaidOrderAsync();
        var audit = new RecordingAuditLog();

        await Handler(c, new RecordingGateway(), audit: audit)
            .Handle(new RefundOrderCommand(c.Order.Id, Amount: 5m), CancellationToken.None);

        // Distinguished from a full refund, because "we gave some of it back" is a different fact.
        Assert.Contains("order.refunded_partial", audit.Actions);
        Assert.DoesNotContain("order.refunded", audit.Actions);
    }

    [Fact]
    public async Task An_attempt_whose_outcome_is_unknown_is_still_recorded()
    {
        var c = await GivenAPaidOrderAsync();
        var audit = new RecordingAuditLog();
        var gateway = new RecordingGateway(PaymentResult.Indeterminate("Mock", "could not be confirmed"));

        await Handler(c, gateway, audit: audit).Handle(new RefundOrderCommand(c.Order.Id), CancellationToken.None);

        // The order did not change, but someone pressed the button and money may have moved. A trail
        // holding only successes would hide exactly the attempt worth finding.
        Assert.Contains("order.refund_unconfirmed", audit.Actions);
    }

    [Fact]
    public async Task A_refusal_is_recorded_too()
    {
        var c = await GivenAPaidOrderAsync();
        var audit = new RecordingAuditLog();
        var gateway = new RecordingGateway(PaymentResult.Declined("Mock", "Charge already refunded."));

        await Handler(c, gateway, audit: audit).Handle(new RefundOrderCommand(c.Order.Id), CancellationToken.None);

        Assert.Contains("order.refund_refused", audit.Actions);
    }

    [Fact]
    public async Task A_payout_the_order_could_not_record_is_reported_and_flagged_for_reconciling()
    {
        var c = await GivenAPaidOrderAsync();
        var audit = new RecordingAuditLog();
        var log = new RecordingLogger<RefundOrderHandler>();

        // Another refund lands between this one reading the order and writing it, so the compare-and-set
        // declines — after the provider has already paid out.
        var gateway = new RecordingGateway(onCharge: () =>
            c.Orders.RecordRefundAsync(c.Order.Id, 29.19m, true, Now, CancellationToken.None).GetAwaiter().GetResult());

        var result = await Handler(c, gateway, log, audit).Handle(new RefundOrderCommand(c.Order.Id), CancellationToken.None);

        // Not silent, and not reported as a plain failure: the money has gone, so this needs a human.
        Assert.True(result.IsFailure);
        Assert.Contains("already changed", result.Error);
        Assert.Contains("order.refund_unrecorded", audit.Actions);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("by hand"));
    }

    [Fact]
    public async Task A_showcase_order_is_refused_so_the_next_visitor_still_sees_it()
    {
        var c = await GivenAPaidOrderAsync();
        c.Orders.Orders.Single().IsProtected = true;
        var gateway = new RecordingGateway();

        var result = await Handler(c, gateway).Handle(new RefundOrderCommand(c.Order.Id), CancellationToken.None);

        // The demo publishes its credentials, so every visitor arrives able to refund. Nothing re-seeds
        // orders, so one click would remove an exhibit permanently.
        Assert.True(result.IsFailure);
        Assert.Contains("showcase orders", result.Error);

        // Refused before the provider is asked, so no money moves either.
        Assert.Equal(0, gateway.Calls);
        Assert.Equal(OrderStatus.Paid, c.Orders.Orders.Single().Status);
    }
}
