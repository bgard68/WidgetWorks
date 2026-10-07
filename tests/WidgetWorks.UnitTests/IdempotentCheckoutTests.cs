using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Checkout.PlaceOrder;
using WidgetWorks.Application.Pricing;
using WidgetWorks.Domain.Catalog;
using WidgetWorks.Domain.Orders;
using WidgetWorks.Infrastructure.Payments;
using WidgetWorks.Infrastructure.Pricing;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// Retry safety on the one call that both takes money and consumes stock.
///
/// The question every test here asks is the same: after N arrivals of one request, how many orders
/// exist? The answer has to be one, whichever order the arrivals interleave in.
/// </summary>
public class IdempotentCheckoutTests
{
    private const string Key = "11111111-2222-3333-4444-555555555555";

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static ShippingAddressInput Address() => new("Jane Doe", "1 Main St", null, "Springfield", "CA", "90001", "US");

    private sealed record Ctx(
        InMemoryCartRepository Carts,
        InMemoryWidgetRepository Widgets,
        InMemoryOrderRepository Orders,
        InMemoryIdempotencyStore Store,
        FakeEmailSender Email,
        Widget Widget,
        Guid CartId);

    private static async Task<Ctx> SetupAsync(int onHand = 100)
    {
        var widgets = new InMemoryWidgetRepository();
        var widget = new Widget { Id = Guid.NewGuid(), Sku = "WW-1", Name = "Gizmo", IsActive = true, Price = 10m, QuantityOnHand = onHand };
        widgets.Store[widget.Id] = widget;

        var carts = new InMemoryCartRepository();
        var cart = await carts.CreateAsync(null, CancellationToken.None);
        await carts.UpsertItemAsync(cart.Id, widget.Id, 2, default, CancellationToken.None);

        return new Ctx(carts, widgets, new InMemoryOrderRepository(widgets), new InMemoryIdempotencyStore(), new FakeEmailSender(), widget, cart.Id);
    }

    private static IdempotentCheckout Handler(Ctx c, IPaymentGateway? gateway = null, IdempotencyOptions? options = null)
    {
        var inner = new CheckoutHandler(
            c.Carts,
            c.Widgets,
            c.Orders,
            new OrderPricer(new FlatRateShippingCalculator(), new StateSalesTaxCalculator(new StaticStateTaxRateProvider())),
            gateway ?? new MockPaymentGateway(),
            c.Email,
            new FakeReconciliationSignal(),
            Clock(),
            NullLogger<CheckoutHandler>.Instance);

        return new IdempotentCheckout(inner, c.Store, options ?? new IdempotencyOptions(), Clock(), NullLogger<IdempotentCheckout>.Instance);
    }

    private static CheckoutCommand Command(Ctx c, string email = "jane@example.com", string? token = "tok_ok")
        => new(c.CartId, null, email, Address(), "Standard", token);

    [Fact]
    public async Task A_repeated_request_replays_the_first_order_instead_of_placing_a_second()
    {
        var c = await SetupAsync();
        var handler = Handler(c);

        var first = await handler.Handle(Key, Command(c), CancellationToken.None);
        var second = await handler.Handle(Key, Command(c), CancellationToken.None);

        Assert.Equal(CheckoutOutcome.Placed, first.Outcome);
        Assert.False(first.Replayed);

        // The same order, not a second one that merely looks similar.
        Assert.Equal(CheckoutOutcome.Placed, second.Outcome);
        Assert.True(second.Replayed);
        Assert.Equal(first.Value!.OrderNumber, second.Value!.OrderNumber);
        Assert.Equal(first.Value!.OrderId, second.Value!.OrderId);
        Assert.Equal(first.Value!.PaymentReference, second.Value!.PaymentReference);

        // The three things a duplicate would have cost: an order, the stock behind it, and a second
        // receipt in the customer's inbox.
        Assert.Single(c.Orders.Orders);
        Assert.Equal(2, c.Widgets.Store[c.Widget.Id].QuantityReserved);
        Assert.Single(c.Email.Sent);
    }

    [Fact]
    public async Task A_key_still_in_flight_is_refused_rather_than_charged_twice()
    {
        var c = await SetupAsync();

        // Hold the first caller inside the payment gateway — the window a double-click lands in.
        using var gate = new SemaphoreSlim(0);
        var gateway = new GatedPaymentGateway(gate);
        var handler = Handler(c, gateway);

        var first = Task.Run(() => handler.Handle(Key, Command(c), CancellationToken.None));
        await gateway.Reached;

        var second = await handler.Handle(Key, Command(c), CancellationToken.None);

        // Conflict, not success and not a decline: the request is fine, it just cannot be answered
        // until the first attempt finishes. Crucially the gateway was not asked a second time.
        Assert.Equal(CheckoutOutcome.Conflict, second.Outcome);
        Assert.Contains("still being processed", second.Error);

        // Flagged transient, which is what lets a client wait and ask again rather than give up.
        Assert.Equal(CheckoutConflictCodes.InFlight, second.ConflictCode);
        Assert.Equal(1, gateway.Charges);

        gate.Release();
        Assert.Equal(CheckoutOutcome.Placed, (await first).Outcome);
        Assert.Single(c.Orders.Orders);
    }

    [Fact]
    public async Task Concurrent_arrivals_of_one_request_produce_exactly_one_order()
    {
        var c = await SetupAsync();

        // Every arrival blocks inside the gateway until all of them are through the claim, which is
        // the interleaving the naive check-then-create version gets wrong.
        using var gate = new SemaphoreSlim(0);
        var gateway = new GatedPaymentGateway(gate);
        var handler = Handler(c, gateway);

        const int Arrivals = 16;
        var calls = Enumerable.Range(0, Arrivals)
            .Select(_ => Task.Run(() => handler.Handle(Key, Command(c), CancellationToken.None)))
            .ToArray();

        // Release generously: only the claim winner ever reaches the gateway, and the extra permits
        // prove it by going unused.
        gate.Release(Arrivals);
        var results = await Task.WhenAll(calls);

        Assert.Single(c.Orders.Orders);
        Assert.Equal(1, gateway.Charges);                                  // one authorization attempt, not sixteen
        Assert.Equal(Arrivals, c.Store.Claims);                            // every arrival did try
        Assert.Equal(1, results.Count(r => r is { Outcome: CheckoutOutcome.Placed, Replayed: false }));

        // Nobody was told something false: the rest either replayed the same order or were asked to
        // retry, and no arrival got a different order number.
        var placed = results.Where(r => r.Outcome == CheckoutOutcome.Placed).ToList();
        Assert.All(placed, r => Assert.Equal(c.Orders.Orders.Single().OrderNumber, r.Value!.OrderNumber));
        Assert.All(
            results.Where(r => r.Outcome != CheckoutOutcome.Placed),
            r => Assert.Equal(CheckoutOutcome.Conflict, r.Outcome));
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_refused_rather_than_answered_with_the_wrong_order()
    {
        var c = await SetupAsync();
        var handler = Handler(c);

        await handler.Handle(Key, Command(c), CancellationToken.None);

        // A different shopper's email under a recycled key. Replaying the first response here would
        // hand one customer another customer's order.
        var reused = await handler.Handle(Key, Command(c, email: "someone.else@example.com"), CancellationToken.None);

        Assert.Equal(CheckoutOutcome.Conflict, reused.Outcome);
        Assert.Contains("already used for a different request", reused.Error);

        // Flagged permanent: retrying this one would only burn attempts.
        Assert.Equal(CheckoutConflictCodes.KeyReused, reused.ConflictCode);
        Assert.Single(c.Orders.Orders);
    }

    [Fact]
    public async Task A_declined_first_attempt_replays_as_the_same_decline()
    {
        var c = await SetupAsync();
        var handler = Handler(c);

        var first = await handler.Handle(Key, Command(c, token: "tok_decline"), CancellationToken.None);
        var retry = await handler.Handle(Key, Command(c, token: "tok_decline"), CancellationToken.None);

        Assert.Equal(CheckoutOutcome.Rejected, first.Outcome);
        Assert.Equal(CheckoutOutcome.Rejected, retry.Outcome);
        Assert.True(retry.Replayed);
        Assert.Equal(first.Error, retry.Error);

        // Storing the failure is the point: re-running it would be a second trip to the gateway for
        // a card that has already said no.
        Assert.Single(c.Orders.Orders);
        Assert.Equal(OrderStatus.PaymentFailed, c.Orders.Orders.Single().Status);
    }

    [Fact]
    public async Task Without_a_key_nothing_changes()
    {
        var c = await SetupAsync();
        var handler = Handler(c);

        var first = await handler.Handle(null, Command(c), CancellationToken.None);

        Assert.Equal(CheckoutOutcome.Placed, first.Outcome);
        Assert.Empty(c.Store.Rows);          // no ledger row written for an unkeyed call

        // And the hole is still open for callers that do not opt in — recorded here so the trade-off
        // is a decision in the suite rather than a surprise in production. A second unkeyed POST of
        // this same cart fails only because checkout deleted the cart; concurrently, both would land.
        var second = await handler.Handle(null, Command(c), CancellationToken.None);
        Assert.Equal(CheckoutOutcome.Rejected, second.Outcome);
        Assert.Equal("Cart not found.", second.Error);
    }

    [Fact]
    public async Task An_over_long_key_is_rejected_before_any_work_happens()
    {
        var c = await SetupAsync();

        var result = await Handler(c, options: new IdempotencyOptions { MaxKeyLength = 64 })
            .Handle(new string('k', 65), Command(c), CancellationToken.None);

        Assert.Equal(CheckoutOutcome.Rejected, result.Outcome);
        Assert.Contains("at most 64 characters", result.Error);
        Assert.Empty(c.Orders.Orders);
        Assert.Empty(c.Store.Rows);
    }

    [Theory]
    [InlineData("key\nPOST /checkout 200 OK")]   // a forged log line
    [InlineData("key\rwith-a-carriage-return")]
    [InlineData("key with a space")]
    [InlineData("key<script>")]
    public async Task A_key_that_could_forge_a_log_entry_is_refused(string key)
    {
        var c = await SetupAsync();

        var result = await Handler(c).Handle(key, Command(c), CancellationToken.None);

        // The key reaches the log, and it comes from a request header. A value carrying newlines could
        // write entries of its own choosing and make them indistinguishable from the real record, so
        // anything outside the characters an opaque token needs is refused rather than escaped.
        Assert.Equal(CheckoutOutcome.Rejected, result.Outcome);
        Assert.Contains("may contain only", result.Error);
        Assert.Empty(c.Orders.Orders);
        Assert.Empty(c.Store.Rows);
    }

    [Fact]
    public async Task Whitespace_around_a_key_does_not_make_it_a_different_key()
    {
        var c = await SetupAsync();
        var handler = Handler(c);

        var first = await handler.Handle(Key, Command(c), CancellationToken.None);
        var padded = await handler.Handle("  " + Key + "  ", Command(c), CancellationToken.None);

        Assert.True(padded.Replayed);
        Assert.Equal(first.Value!.OrderNumber, padded.Value!.OrderNumber);
        Assert.Single(c.Orders.Orders);
    }

    [Fact]
    public async Task Retention_forgets_old_keys_and_keeps_live_ones()
    {
        var c = await SetupAsync();
        var clock = Clock();
        var options = new IdempotencyOptions { RetentionHours = 24 };

        var scope = $"checkout:{c.CartId:N}";
        await c.Store.TryClaimAsync(scope, "old", "hash", clock.GetUtcNow().AddHours(-25), CancellationToken.None);
        await c.Store.TryClaimAsync(scope, "fresh", "hash", clock.GetUtcNow().AddHours(-1), CancellationToken.None);

        var purged = await new PurgeIdempotencyKeysHandler(c.Store, options, clock, NullLogger<PurgeIdempotencyKeysHandler>.Instance)
            .Handle(CancellationToken.None);

        Assert.Equal(1, purged);
        Assert.Equal($"checkout:{c.CartId:N}|fresh", Assert.Single(c.Store.Rows.Keys));
    }

    /// <summary>
    /// A gateway that waits to be let through, so a test can hold every concurrent arrival inside
    /// the charge at once, and that counts how many times it was actually called.
    /// </summary>
    private sealed class GatedPaymentGateway(SemaphoreSlim gate) : IPaymentGateway
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _charges;

        public int Charges => Volatile.Read(ref _charges);

        /// <summary>Completes once a caller is inside the charge — a signal to wait on, never a sleep.</summary>
        public Task Reached => _reached.Task;

        public string Name => "Gated";

        public async Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _charges);
            _reached.TrySetResult();
            await gate.WaitAsync(ct);
            return PaymentResult.Ok(Name, "gated_" + Guid.NewGuid().ToString("N")[..10]);
        }

        /// <summary>Never reached: nothing here produces an indeterminate charge to reconcile.</summary>
        public Task<PaymentResult> ProbeAsync(string orderNumber, CancellationToken ct)
            => Task.FromResult(PaymentResult.Indeterminate(Name, "not probed"));

        /// <summary>Never reached: these tests place orders, they do not refund them.</summary>
        public Task<PaymentResult> RefundAsync(string reference, decimal amount, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(PaymentResult.Declined(Name, "not refundable"));
    }
}
