namespace WidgetWorks.Application.Abstractions;

public sealed record PaymentRequest(string OrderNumber, decimal Amount, string Currency, string Email, string? PaymentToken);

/// <summary>Outcome of an authorization attempt. Pending means the provider is settling asynchronously
/// (redirect/BNPL) and a webhook will later confirm success or failure.</summary>
public enum PaymentStatus
{
    Succeeded,
    Pending,
    Declined,
}

public sealed record PaymentResult(
    PaymentStatus Status,
    string Provider,
    string? Reference,
    string? Error,
    string? ClientSecret = null,
    string? NextActionUrl = null)
{
    /// <summary>True only for a fully-settled, successful charge (synchronous path).</summary>
    public bool Success => Status == PaymentStatus.Succeeded;

    /// <summary>True when the charge is authorized but settling asynchronously (awaiting a webhook).</summary>
    public bool IsPending => Status == PaymentStatus.Pending;

    public static PaymentResult Ok(string provider, string reference) => new(PaymentStatus.Succeeded, provider, reference, null);

    public static PaymentResult Declined(string provider, string error) => new(PaymentStatus.Declined, provider, null, error);

    /// <summary>Authorized but not yet settled; a provider webhook finalizes the order. The reference
    /// (e.g. the PaymentIntent id) is persisted so the webhook can correlate back to the order.</summary>
    public static PaymentResult Pending(string provider, string reference, string? clientSecret = null, string? nextActionUrl = null)
        => new(PaymentStatus.Pending, provider, reference, null, clientSecret, nextActionUrl);
}

/// <summary>A payment provider. Mock and Stripe adapters sit behind this seam.</summary>
public interface IPaymentGateway
{
    string Name { get; }

    Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct);
}
