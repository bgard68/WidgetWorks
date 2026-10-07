namespace WidgetWorks.Application.Abstractions;

public sealed record PaymentRequest(string OrderNumber, decimal Amount, string Currency, string Email, string? PaymentToken);

/// <summary>Outcome of an authorization attempt. Pending means the provider is settling asynchronously
/// (redirect/BNPL) and a webhook will later confirm success or failure.</summary>
public enum PaymentStatus
{
    Succeeded,
    Pending,
    Declined,

    /// <summary>
    /// The provider never gave an outcome — it timed out, or failed every attempt with a 5xx. The
    /// charge may or may not have been taken.
    ///
    /// This is deliberately not folded into <see cref="Declined"/>, which is the mistake it exists to
    /// prevent: declining releases the stock reservation and tells the customer their payment failed,
    /// and doing that to a charge that actually succeeded is the worst outcome this system has. An
    /// indeterminate charge keeps its reservation and is settled later by reconciliation.
    /// </summary>
    Indeterminate,
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

    /// <summary>True when the provider gave no outcome, so the charge must be reconciled later.</summary>
    public bool IsIndeterminate => Status == PaymentStatus.Indeterminate;

    /// <summary>True when the charge is authorized but settling asynchronously (awaiting a webhook).</summary>
    public bool IsPending => Status == PaymentStatus.Pending;

    public static PaymentResult Ok(string provider, string reference) => new(PaymentStatus.Succeeded, provider, reference, null);

    public static PaymentResult Declined(string provider, string error) => new(PaymentStatus.Declined, provider, null, error);

    /// <summary>Authorized but not yet settled; a provider webhook finalizes the order. The reference
    /// (e.g. the PaymentIntent id) is persisted so the webhook can correlate back to the order.</summary>
    public static PaymentResult Pending(string provider, string reference, string? clientSecret = null, string? nextActionUrl = null)
        => new(PaymentStatus.Pending, provider, reference, null, clientSecret, nextActionUrl);

    /// <summary>
    /// No outcome was obtained. Carries no reference, because there is nothing to correlate on yet —
    /// finding that out is reconciliation's job.
    /// </summary>
    public static PaymentResult Indeterminate(string provider, string error)
        => new(PaymentStatus.Indeterminate, provider, null, error);
}

/// <summary>A payment provider. Mock and Stripe adapters sit behind this seam.</summary>
public interface IPaymentGateway
{
    string Name { get; }

    Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct);

    /// <summary>
    /// Asks the provider what actually became of the charge for <paramref name="orderNumber"/>,
    /// without creating one. The distinction is the whole contract: reconciliation runs against
    /// orders whose charge may never have happened, so a probe that could charge would turn an
    /// unknown outcome into a real one nobody asked for.
    ///
    /// Returns <see cref="PaymentStatus.Indeterminate"/> when the provider still cannot say — which
    /// includes "no charge found yet", because provider search indexes lag behind writes and a
    /// missing record is not proof of absence.
    /// </summary>
    Task<PaymentResult> ProbeAsync(string orderNumber, CancellationToken ct);

    /// <summary>
    /// Returns money already taken. <paramref name="reference"/> is the charge's provider id, and
    /// <paramref name="idempotencyKey"/> makes the call repeatable — a retried refund must give the
    /// money back once, not twice.
    ///
    /// <see cref="PaymentStatus.Indeterminate"/> means the refund could not be confirmed, which a
    /// caller must not read as "not refunded": the only safe response is to leave the order alone and
    /// have a human check the provider, exactly as on the charge path.
    /// </summary>
    Task<PaymentResult> RefundAsync(string reference, decimal amount, string idempotencyKey, CancellationToken ct);
}
