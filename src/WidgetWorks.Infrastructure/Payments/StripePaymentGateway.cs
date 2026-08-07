using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Payments;

public sealed class StripeOptions
{
    public string SecretKey { get; set; } = string.Empty;

    public string ApiBase { get; set; } = "https://api.stripe.com";
}

/// <summary>
/// Stripe test-mode adapter. Creates and confirms a PaymentIntent via Stripe's REST API using an
/// HttpClient (no SDK dependency). The secret key comes only from configuration / user-secrets and is
/// never committed. Selected by config (Payments:Provider = Stripe); the Mock gateway is the default.
/// </summary>
public sealed class StripePaymentGateway(HttpClient http, IOptions<StripeOptions> options) : IPaymentGateway
{
    public string Name => "Stripe";

    public async Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.SecretKey))
        {
            return PaymentResult.Declined(Name, "Stripe is not configured.");
        }

        if (request.Amount <= 0)
        {
            return PaymentResult.Declined(Name, "Amount must be positive.");
        }

        var amountMinor = (long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero);
        var form = new Dictionary<string, string>
        {
            ["amount"] = amountMinor.ToString(CultureInfo.InvariantCulture),
            ["currency"] = string.IsNullOrWhiteSpace(request.Currency) ? "usd" : request.Currency,
            ["confirm"] = "true",
            ["payment_method"] = string.IsNullOrWhiteSpace(request.PaymentToken) ? "pm_card_visa" : request.PaymentToken!,
            ["description"] = $"WidgetWorks order {request.OrderNumber}",
            ["automatic_payment_methods[enabled]"] = "true",
            ["automatic_payment_methods[allow_redirects]"] = "never",
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiBase}/v1/payment_intents")
        {
            Content = new FormUrlEncodedContent(form),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SecretKey);

        using var response = await http.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            return PaymentResult.Declined(Name, $"Stripe returned {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
        return string.Equals(status, "succeeded", StringComparison.Ordinal)
            ? PaymentResult.Ok(Name, id ?? "unknown")
            : PaymentResult.Declined(Name, $"Payment not completed (status: {status}).");
    }
}
