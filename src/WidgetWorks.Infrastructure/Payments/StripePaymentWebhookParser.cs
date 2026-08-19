using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Payments;

/// <summary>
/// Verifies a Stripe webhook using the Stripe-Signature header (t=timestamp,v1=hmacSHA256 of
/// "{t}.{payload}" keyed by the whsec_ secret) and maps payment_intent.succeeded / .payment_failed /
/// .canceled to a normalized event keyed by the PaymentIntent id.
/// </summary>
public sealed class StripePaymentWebhookParser(IOptions<StripeOptions> options) : IPaymentWebhookParser
{
    public string Provider => "Stripe";

    public IReadOnlyList<string> SignatureHeaders { get; } = ["Stripe-Signature"];

    public bool TryParse(string payload, string? signatureHeader, out PaymentEvent? evt, out string? error)
    {
        evt = null;
        error = null;

        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            error = "Stripe webhook secret is not configured.";
            return false;
        }

        if (!VerifySignature(payload, signatureHeader, secret))
        {
            error = "Invalid webhook signature.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            PaymentEventType eventType;
            switch (type)
            {
                case "payment_intent.succeeded":
                    eventType = PaymentEventType.Succeeded;
                    break;
                case "payment_intent.payment_failed":
                case "payment_intent.canceled":
                    eventType = PaymentEventType.Failed;
                    break;
                default:
                    error = $"Unhandled event type '{type}'.";
                    return false;
            }

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("object", out var obj) ||
                !obj.TryGetProperty("id", out var idEl) ||
                idEl.GetString() is not { Length: > 0 } reference)
            {
                error = "Missing PaymentIntent id.";
                return false;
            }

            evt = new PaymentEvent(Provider, reference, eventType);
            return true;
        }
        catch (JsonException)
        {
            error = "Malformed webhook payload.";
            return false;
        }
    }

    private static bool VerifySignature(string payload, string? header, string secret)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        string? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            var key = kv[0].Trim();
            var value = kv[1].Trim();
            if (key == "t")
            {
                timestamp = value;
            }
            else if (key == "v1")
            {
                signatures.Add(value);
            }
        }

        if (timestamp is null || signatures.Count == 0)
        {
            return false;
        }

        var signedPayload = $"{timestamp}.{payload}";
        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signedPayload)));
        var expectedBytes = Encoding.ASCII.GetBytes(expected);

        foreach (var candidate in signatures)
        {
            var candidateBytes = Encoding.ASCII.GetBytes(candidate.ToLowerInvariant());
            if (candidateBytes.Length == expectedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes))
            {
                return true;
            }
        }

        return false;
    }
}
