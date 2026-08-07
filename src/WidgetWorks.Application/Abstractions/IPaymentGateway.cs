namespace WidgetWorks.Application.Abstractions;

public sealed record PaymentRequest(string OrderNumber, decimal Amount, string Currency, string Email, string? PaymentToken);

public sealed record PaymentResult(bool Success, string Provider, string? Reference, string? Error)
{
    public static PaymentResult Ok(string provider, string reference) => new(true, provider, reference, null);

    public static PaymentResult Declined(string provider, string error) => new(false, provider, null, error);
}

/// <summary>A payment provider. Mock and Stripe adapters sit behind this seam.</summary>
public interface IPaymentGateway
{
    string Name { get; }

    Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct);
}
