using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Infrastructure.Payments;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The Stripe adapter, driven through a stub transport. Two things are worth pinning down: the
/// request Stripe actually receives (amount in minor units, the order number in metadata so the
/// webhook can find the order again), and the mapping from PaymentIntent status to the three
/// outcomes checkout branches on — because a status mapped to the wrong branch either ships goods
/// that were never paid for or cancels an order that was.
/// </summary>
public class StripeGatewayTests
{
    private const string Succeeded = """{"id":"pi_1","status":"succeeded"}""";

    private static (StripePaymentGateway Gateway, StubHandler Handler) Build(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = Succeeded,
        string secretKey = "sk_test_key")
        => BuildScripted([new Reply(status, body)], secretKey);

    /// <summary>
    /// A gateway whose transport hands back the given replies in order, the last one repeating. Retry
    /// delays are zeroed so the suite does not actually wait — the shipped defaults are asserted
    /// separately, in <see cref="The_shipped_retry_budget_is_short_enough_to_hold_a_checkout_open"/>.
    /// </summary>
    private static (StripePaymentGateway Gateway, StubHandler Handler) BuildScripted(
        Reply[] replies,
        string secretKey = "sk_test_key",
        int[]? delays = null)
    {
        var handler = new StubHandler(replies);
        var gateway = new StripePaymentGateway(
            new HttpClient(handler),
            Options.Create(new StripeOptions
            {
                SecretKey = secretKey,
                ApiBase = "https://api.stripe.test",
                RetryDelaysMs = delays ?? [0, 0],
            }),
            NullLogger<StripePaymentGateway>.Instance);
        return (gateway, handler);
    }

    /// <summary>One scripted reply: a status and body, or an exception instead of an answer.</summary>
    private sealed record Reply(HttpStatusCode Status, string Body = "{}", Exception? Throws = null, string? RetryAfterSeconds = null);

    private static PaymentRequest Request(decimal amount = 29.19m, string? token = "pm_card_visa")
        => new("WW-20260501-ABC123", amount, "usd", "jane@example.com", token);

    [Fact]
    public async Task It_declines_without_calling_stripe_when_no_key_is_configured()
    {
        var (gateway, handler) = Build(secretKey: "");

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        Assert.Equal(PaymentStatus.Declined, result.Status);
        Assert.Equal("Stripe is not configured.", result.Error);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task It_refuses_a_non_positive_amount_without_calling_stripe(decimal amount)
    {
        var (gateway, handler) = Build();

        var result = await gateway.ChargeAsync(Request(amount), CancellationToken.None);

        Assert.Equal(PaymentStatus.Declined, result.Status);
        Assert.Equal("Amount must be positive.", result.Error);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task It_sends_the_amount_in_minor_units_and_the_order_number_in_metadata()
    {
        var (gateway, handler) = Build();

        await gateway.ChargeAsync(Request(29.19m), CancellationToken.None);

        Assert.Contains("amount=2919", handler.LastBody);
        Assert.Contains("currency=usd", handler.LastBody);
        Assert.Contains("payment_method=pm_card_visa", handler.LastBody);

        // How the webhook correlates back to the order later.
        Assert.Contains("WW-20260501-ABC123", handler.LastBody);
        Assert.Equal("Bearer sk_test_key", handler.LastAuthorization);
        Assert.Contains("/v1/payment_intents", handler.LastUrl);
    }

    [Fact]
    public async Task Rounding_to_minor_units_is_half_away_from_zero()
    {
        var (gateway, handler) = Build();

        await gateway.ChargeAsync(Request(10.005m), CancellationToken.None);

        // 1000.5 minor units must bill as 1001, not 1000.
        Assert.Contains("amount=1001", handler.LastBody);
    }

    [Fact]
    public async Task A_missing_token_falls_back_to_the_test_card()
    {
        var (gateway, handler) = Build();

        await gateway.ChargeAsync(Request(token: null), CancellationToken.None);

        Assert.Contains("payment_method=pm_card_visa", handler.LastBody);
    }

    [Fact]
    public async Task A_succeeded_intent_is_a_completed_payment()
    {
        var (gateway, _) = Build(body: """{"id":"pi_abc","status":"succeeded"}""");

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        Assert.Equal(PaymentStatus.Succeeded, result.Status);
        Assert.Equal("pi_abc", result.Reference);
        Assert.Equal("Stripe", result.Provider);
    }

    [Theory]
    [InlineData("requires_action")]
    [InlineData("requires_confirmation")]
    [InlineData("processing")]
    public async Task An_unsettled_intent_parks_the_order_rather_than_failing_it(string status)
    {
        var (gateway, _) = Build(body: $$"""{"id":"pi_abc","status":"{{status}}","client_secret":"cs_123"}""");

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        Assert.Equal(PaymentStatus.Pending, result.Status);
        Assert.Equal("cs_123", result.ClientSecret);
    }

    [Fact]
    public async Task A_redirect_url_is_pulled_out_of_next_action()
    {
        const string body = """
            {"id":"pi_abc","status":"requires_action","next_action":{"redirect_to_url":{"url":"https://hooks.test/go"}}}
            """;
        var (gateway, _) = Build(body: body);

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        Assert.Equal("https://hooks.test/go", result.NextActionUrl);
    }

    [Theory]
    [InlineData("""{"id":"pi_abc","status":"requires_action"}""")]
    [InlineData("""{"id":"pi_abc","status":"requires_action","next_action":null}""")]
    [InlineData("""{"id":"pi_abc","status":"requires_action","next_action":{"type":"use_stripe_sdk"}}""")]
    public async Task A_missing_redirect_is_null_rather_than_a_crash(string body)
    {
        var (gateway, _) = Build(body: body);

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        Assert.Equal(PaymentStatus.Pending, result.Status);
        Assert.Null(result.NextActionUrl);
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("requires_payment_method")]
    [InlineData("")]
    public async Task Any_other_status_is_a_decline(string status)
    {
        var (gateway, _) = Build(body: $$"""{"id":"pi_abc","status":"{{status}}"}""");

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        Assert.Equal(PaymentStatus.Declined, result.Status);
        Assert.Contains(status, result.Error);
    }

    [Fact]
    public async Task An_http_error_is_a_decline_carrying_the_status_code()
    {
        var (gateway, _) = Build(HttpStatusCode.PaymentRequired, """{"error":{"message":"card declined"}}""");

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        Assert.Equal(PaymentStatus.Declined, result.Status);
        Assert.Equal("Stripe returned 402.", result.Error);
    }

    [Fact]
    public async Task An_intent_with_no_id_still_produces_a_usable_reference()
    {
        var (gateway, _) = Build(body: """{"status":"succeeded"}""");

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        Assert.Equal(PaymentStatus.Succeeded, result.Status);
        Assert.Equal("unknown", result.Reference);
    }

    [Fact]
    public void The_adapter_names_itself_so_the_webhook_route_can_match_it()
    {
        var (gateway, _) = Build();

        Assert.Equal("Stripe", gateway.Name);
    }

    /// <summary>Records what was sent and replies with a canned response — no network involved.</summary>
    [Fact]
    public async Task It_sends_an_idempotency_key_derived_from_the_order_number()
    {
        var (gateway, handler) = Build();

        await gateway.ChargeAsync(Request(), CancellationToken.None);

        // Our own ledger cannot protect this call — it stops a duplicate request arriving at us, and
        // is blind to a retry of the outbound one. Without this header a retried create is a second
        // charge.
        Assert.Equal("order-WW-20260501-ABC123", Assert.Single(handler.Keys));
    }

    [Fact]
    public async Task It_retries_a_server_error_under_the_very_same_key()
    {
        var (gateway, handler) = BuildScripted([
            new Reply(HttpStatusCode.InternalServerError),
            new Reply(HttpStatusCode.BadGateway),
            new Reply(HttpStatusCode.OK, Succeeded),
        ]);

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        Assert.Equal(PaymentStatus.Succeeded, result.Status);
        Assert.Equal(3, handler.Calls);

        // One distinct key across all three. If this ever became three keys, the two failed attempts
        // could each have left a charge behind.
        Assert.Single(handler.Keys.Distinct());
    }

    [Fact]
    public async Task It_retries_a_connection_that_never_answered()
    {
        var (gateway, handler) = BuildScripted([
            new Reply(HttpStatusCode.OK, Throws: new HttpRequestException("socket closed")),
            new Reply(HttpStatusCode.OK, Succeeded),
        ]);

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        Assert.Equal(PaymentStatus.Succeeded, result.Status);
        Assert.Equal(2, handler.Calls);
        Assert.Single(handler.Keys.Distinct());
    }

    [Fact]
    public async Task It_retries_a_client_timeout_but_not_a_cancelled_caller()
    {
        var (gateway, handler) = BuildScripted([
            new Reply(HttpStatusCode.OK, Throws: new TaskCanceledException("timed out")),
            new Reply(HttpStatusCode.OK, Succeeded),
        ]);

        Assert.Equal(PaymentStatus.Succeeded, (await gateway.ChargeAsync(Request(), CancellationToken.None)).Status);
        Assert.Equal(2, handler.Calls);

        // A caller who went away is a different matter: retrying a charge on behalf of someone who
        // abandoned the request is work nobody asked for, so cancellation propagates.
        var (cancelling, cancelledHandler) = BuildScripted([new Reply(HttpStatusCode.OK, Throws: new TaskCanceledException("cancelled"))]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.ChargeAsync(Request(), cancelled.Token));
        Assert.Equal(1, cancelledHandler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task It_never_retries_an_answer_stripe_actually_gave(HttpStatusCode status)
    {
        var (gateway, handler) = BuildScripted([new Reply(status)]);

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        // A declined card is a decision, not a blip. Retrying one only annoys the issuer, and three
        // attempts at a bad secret key is three times the noise in someone's logs.
        Assert.Equal(PaymentStatus.Declined, result.Status);
        Assert.Equal(1, handler.Calls);
        Assert.Equal($"Stripe returned {(int)status}.", result.Error);
    }

    [Fact]
    public async Task It_honours_a_retry_after_asked_for_by_a_rate_limit()
    {
        var (gateway, handler) = BuildScripted(
            [new Reply(HttpStatusCode.TooManyRequests, RetryAfterSeconds: "1"), new Reply(HttpStatusCode.OK, Succeeded)],
            delays: [0, 0]);

        var clock = Stopwatch.StartNew();
        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);
        clock.Stop();

        Assert.Equal(PaymentStatus.Succeeded, result.Status);
        Assert.Equal(2, handler.Calls);

        // The configured backoff here is zero, so any real wait can only have come from the header.
        // Ignoring Retry-After is how a rate-limited client earns a ban instead of a slowdown.
        Assert.True(clock.ElapsedMilliseconds >= 500, $"waited only {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task An_unreachable_provider_reads_differently_from_a_refusal()
    {
        var (gateway, handler) = BuildScripted([new Reply(HttpStatusCode.OK, Throws: new HttpRequestException("no route to host"))]);

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        // Nothing ever reached Stripe, so no charge can exist and declining abandons no money.
        Assert.Equal(PaymentStatus.Declined, result.Status);
        Assert.Equal("The payment provider could not be reached.", result.Error);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task An_unconfirmable_charge_is_reported_as_indeterminate_rather_than_declined()
    {
        var (gateway, handler) = BuildScripted([new Reply(HttpStatusCode.ServiceUnavailable)]);

        var result = await gateway.ChargeAsync(Request(), CancellationToken.None);

        Assert.Equal(3, handler.Calls);

        // Not Declined, and this is the whole point. Stripe answered without giving an outcome, so the
        // money may already be gone — and a decline would release the stock and tell the customer the
        // payment failed. Checkout parks it for reconciliation instead.
        Assert.Equal(PaymentStatus.Indeterminate, result.Status);
        Assert.Null(result.Reference);
        Assert.Contains("no outcome", result.Error);
    }

    [Fact]
    public async Task Probing_searches_by_order_number_and_never_creates_a_charge()
    {
        var (gateway, handler) = BuildScripted([
            new Reply(HttpStatusCode.OK, """{"data":[{"id":"pi_found","status":"succeeded"}]}"""),
        ]);

        var result = await gateway.ProbeAsync("WW-20260501-ABC123", CancellationToken.None);

        Assert.Equal(PaymentStatus.Succeeded, result.Status);
        Assert.Equal("pi_found", result.Reference);

        // A search, and specifically a GET. Replaying the create under the same key would return the
        // original intent when one exists — but would *make* one when it does not, and the orders being
        // probed are exactly those that may never have been charged.
        Assert.Contains("/v1/payment_intents/search", handler.LastUrl);
        Assert.Contains(Uri.EscapeDataString("metadata['order_number']:'WW-20260501-ABC123'"), handler.LastUrl);
        Assert.Equal(string.Empty, handler.LastBody);
    }

    [Theory]
    [InlineData("canceled", PaymentStatus.Declined)]
    [InlineData("requires_payment_method", PaymentStatus.Declined)]
    [InlineData("processing", PaymentStatus.Pending)]
    [InlineData("requires_action", PaymentStatus.Pending)]
    [InlineData("something_new", PaymentStatus.Indeterminate)]
    public async Task Probing_maps_each_charge_state_to_what_may_safely_be_done_about_it(string status, PaymentStatus expected)
    {
        var (gateway, _) = BuildScripted([
            new Reply(HttpStatusCode.OK, $$"""{"data":[{"id":"pi_1","status":"{{status}}"}]}"""),
        ]);

        var result = await gateway.ProbeAsync("WW-20260501-ABC123", CancellationToken.None);

        // An unrecognised status maps to Indeterminate on purpose: a provider adding a state we have
        // never seen must not cause an order to be failed on a guess.
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task Probing_treats_nothing_found_as_still_unknown_not_as_a_refusal()
    {
        var (gateway, _) = BuildScripted([new Reply(HttpStatusCode.OK, """{"data":[]}""")]);

        var result = await gateway.ProbeAsync("WW-20260501-ABC123", CancellationToken.None);

        // Stripe's search index lags its own writes by up to a minute. Reading an empty result as
        // "never charged" would fail orders that had been paid seconds earlier — so the order is left
        // alone for the next pass.
        Assert.Equal(PaymentStatus.Indeterminate, result.Status);
        Assert.Contains("No charge found", result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_probe_that_fails_decides_nothing(HttpStatusCode status)
    {
        var (gateway, _) = BuildScripted([new Reply(status)]);

        var result = await gateway.ProbeAsync("WW-20260501-ABC123", CancellationToken.None);

        Assert.Equal(PaymentStatus.Indeterminate, result.Status);
    }

    [Fact]
    public async Task A_probe_cannot_be_answered_without_a_configured_key()
    {
        var (gateway, handler) = BuildScripted([new Reply(HttpStatusCode.OK)], secretKey: "");

        var result = await gateway.ProbeAsync("WW-20260501-ABC123", CancellationToken.None);

        // Indeterminate rather than declined: a missing key is our misconfiguration, and must not be
        // turned into a verdict on someone's payment.
        Assert.Equal(PaymentStatus.Indeterminate, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_probe_that_cannot_reach_stripe_decides_nothing()
    {
        var (gateway, _) = BuildScripted([new Reply(HttpStatusCode.OK, Throws: new HttpRequestException("down"))]);

        var result = await gateway.ProbeAsync("WW-20260501-ABC123", CancellationToken.None);

        Assert.Equal(PaymentStatus.Indeterminate, result.Status);
        Assert.Contains("could not be reached", result.Error);
    }

    [Fact]
    public void The_shipped_retry_budget_is_short_enough_to_hold_a_checkout_open()
    {
        var delays = new StripeOptions().RetryDelaysMs;

        // Retrying at all is only safe because of the key, so this list existing is a claim about
        // that. It is also bounded on purpose: a shopper is watching a spinner while it runs.
        Assert.NotEmpty(delays);
        Assert.True(delays.Sum() <= 3000, $"retry budget of {delays.Sum()} ms is too long to make a shopper wait");
    }

    private sealed class StubHandler(Reply[] replies) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string LastBody { get; private set; } = string.Empty;

        public string LastUrl { get; private set; } = string.Empty;

        public string? LastAuthorization { get; private set; }

        /// <summary>The Idempotency-Key seen on each attempt, in order — the point of the retry tests.</summary>
        public List<string?> Keys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = request.RequestUri?.ToString() ?? string.Empty;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Keys.Add(request.Headers.TryGetValues("Idempotency-Key", out var key) ? string.Join(",", key) : null);

            // The last scripted reply repeats, so a test can say "always fails" with one entry.
            var reply = replies[Math.Min(Calls - 1, replies.Length - 1)];
            if (reply.Throws is not null)
            {
                throw reply.Throws;
            }

            var response = new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body) };
            if (reply.RetryAfterSeconds is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", reply.RetryAfterSeconds);
            }

            return response;
        }
    }
}
