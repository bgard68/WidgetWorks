using System.Net;
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
    private static (StripePaymentGateway Gateway, StubHandler Handler) Build(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = """{"id":"pi_1","status":"succeeded"}""",
        string secretKey = "sk_test_key")
    {
        var handler = new StubHandler(status, body);
        var gateway = new StripePaymentGateway(
            new HttpClient(handler),
            Options.Create(new StripeOptions { SecretKey = secretKey, ApiBase = "https://api.stripe.test" }));
        return (gateway, handler);
    }

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
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string LastBody { get; private set; } = string.Empty;

        public string LastUrl { get; private set; } = string.Empty;

        public string? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = request.RequestUri?.ToString() ?? string.Empty;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
