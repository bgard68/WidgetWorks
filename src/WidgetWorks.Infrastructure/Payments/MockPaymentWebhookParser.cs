using System.Text.Json;
using Microsoft.Extensions.Options;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Payments;

/// <summary>
/// Parses the demo webhook: a JSON body of {"reference":"mock_pi_...","outcome":"succeeded|failed"}.
/// Verification is a simple shared-secret header (X-Webhook-Signature) that is skipped when no secret
/// is configured — enough to demo the flow locally without a provider account.
/// </summary>
public sealed class MockPaymentWebhookParser(IOptions<MockPaymentOptions> options) : IPaymentWebhookParser
{
    public string Provider => "Mock";

    public IReadOnlyList<string> SignatureHeaders { get; } = ["X-Webhook-Signature"];

    public bool TryParse(string payload, string? signatureHeader, out PaymentEvent? evt, out string? error)
    {
        evt = null;
        error = null;

        var secret = options.Value.WebhookSecret;
        if (!string.IsNullOrEmpty(secret) && !string.Equals(secret, signatureHeader, StringComparison.Ordinal))
        {
            error = "Invalid webhook signature.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "Empty webhook payload.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var reference = root.TryGetProperty("reference", out var r) ? r.GetString() : null;
            if (string.IsNullOrWhiteSpace(reference))
            {
                error = "Missing 'reference'.";
                return false;
            }

            var outcome = root.TryGetProperty("outcome", out var o) ? o.GetString() : null;
            var type = string.Equals(outcome, "failed", StringComparison.OrdinalIgnoreCase)
                ? PaymentEventType.Failed
                : PaymentEventType.Succeeded;

            evt = new PaymentEvent(Provider, reference, type);
            return true;
        }
        catch (JsonException)
        {
            error = "Malformed webhook payload.";
            return false;
        }
    }
}
