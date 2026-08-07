using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Payments;

/// <summary>
/// Default payment gateway for the demo. Approves any positive charge and returns a synthetic
/// reference, EXCEPT when the payment token contains "decline" (or the standard test decline PAN),
/// which lets reviewers exercise the failure path deterministically. No real money, no network.
/// </summary>
public sealed class MockPaymentGateway : IPaymentGateway
{
    public string Name => "Mock";

    public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct)
    {
        if (request.Amount <= 0)
        {
            return Task.FromResult(PaymentResult.Declined(Name, "Amount must be positive."));
        }

        var token = request.PaymentToken?.Trim() ?? string.Empty;
        if (token.Contains("decline", StringComparison.OrdinalIgnoreCase) || token == "4000000000000002")
        {
            return Task.FromResult(PaymentResult.Declined(Name, "Your card was declined."));
        }

        var reference = "mock_" + Guid.NewGuid().ToString("N");
        return Task.FromResult(PaymentResult.Ok(Name, reference));
    }
}
