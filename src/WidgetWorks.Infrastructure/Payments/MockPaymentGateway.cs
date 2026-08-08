using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Payments;

/// <summary>Options for the demo payment gateway + its webhook. No real money, no network.</summary>
public sealed class MockPaymentOptions
{
    /// <summary>
    /// Optional shared secret. When set, the mock webhook requires a matching X-Webhook-Signature
    /// header. Empty by default so the local demo/smoke test can post webhooks without a signature.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;
}

/// <summary>
/// Default payment gateway for the demo. Approves any positive charge synchronously and returns a
/// synthetic reference, EXCEPT:
///   - tokens containing "decline" (or the standard test decline PAN) are declined; and
///   - tokens for an asynchronous method (BNPL/redirect, e.g. "klarna...", "bnpl...", "async...")
///     return Pending, so reviewers can exercise the AwaitingPayment -> webhook -> Paid/Failed flow
///     deterministically without any provider account.
/// </summary>
public sealed class MockPaymentGateway : IPaymentGateway
{
    private static readonly string[] AsyncTokenMarkers =
        ["async", "bnpl", "klarna", "afterpay", "affirm", "paylater", "pay-later", "pending"];

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

        if (IsAsyncToken(token))
        {
            // Authorized but settling asynchronously; a webhook to /webhooks/payments/mock finalizes it.
            var intentRef = "mock_pi_" + Guid.NewGuid().ToString("N");
            return Task.FromResult(PaymentResult.Pending(Name, intentRef, clientSecret: intentRef + "_secret"));
        }

        var reference = "mock_" + Guid.NewGuid().ToString("N");
        return Task.FromResult(PaymentResult.Ok(Name, reference));
    }

    private static bool IsAsyncToken(string token)
    {
        foreach (var marker in AsyncTokenMarkers)
        {
            if (token.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
