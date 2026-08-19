namespace WidgetWorks.Application.Abstractions;

public enum PaymentEventType
{
    Succeeded,
    Failed,
}

/// <summary>A normalized inbound payment event, correlated to an order by (Provider, Reference).</summary>
public sealed record PaymentEvent(string Provider, string Reference, PaymentEventType Type);

/// <summary>
/// Verifies and parses a provider's webhook payload into a normalized <see cref="PaymentEvent"/>.
/// One implementation per provider; the WebApi selects it by the {provider} route segment.
/// Returning false signals an unverifiable or malformed request (the endpoint answers 400).
/// </summary>
public interface IPaymentWebhookParser
{
    /// <summary>Provider key, matched case-insensitively against the gateway Name and the route segment.</summary>
    string Provider { get; }

    /// <summary>
    /// Request header(s) this provider delivers its signature in, tried in order. Kept here rather
    /// than in the endpoint so transport code never has to know one provider's header name.
    /// </summary>
    IReadOnlyList<string> SignatureHeaders { get; }

    bool TryParse(string payload, string? signatureHeader, out PaymentEvent? evt, out string? error);
}
