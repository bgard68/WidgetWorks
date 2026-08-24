using WidgetWorks.Application.Abstractions;
using WidgetWorks.Infrastructure.Payments;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The demo gateway's contract: deterministic outcomes keyed off the payment token, so every
/// payment path (paid, declined, parked-for-webhook) can be walked without a provider account.
/// </summary>
public class MockPaymentGatewayTests
{
    private static PaymentRequest Request(decimal amount = 10m, string? token = "tok_ok")
        => new("WW-1", amount, "usd", "jane@example.com", token);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_non_positive_amount_is_declined(decimal amount)
    {
        var result = await new MockPaymentGateway().ChargeAsync(Request(amount), CancellationToken.None);

        Assert.Equal(PaymentStatus.Declined, result.Status);
        Assert.False(result.Success);
        Assert.False(result.IsPending);
        Assert.Equal("Amount must be positive.", result.Error);
    }

    [Theory]
    [InlineData("tok_decline")]
    [InlineData("4000000000000002")]
    public async Task Decline_tokens_are_declined(string token)
    {
        var result = await new MockPaymentGateway().ChargeAsync(Request(token: token), CancellationToken.None);

        Assert.Equal(PaymentStatus.Declined, result.Status);
        Assert.Equal("Your card was declined.", result.Error);
    }

    [Fact]
    public async Task An_async_method_token_parks_the_charge_as_pending()
    {
        var result = await new MockPaymentGateway().ChargeAsync(Request(token: "klarna_demo"), CancellationToken.None);

        Assert.True(result.IsPending);
        Assert.False(result.Success);
        Assert.StartsWith("mock_pi_", result.Reference);
        Assert.EndsWith("_secret", result.ClientSecret);
    }

    [Fact]
    public async Task Anything_else_is_approved_synchronously()
    {
        var result = await new MockPaymentGateway().ChargeAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.IsPending);
        Assert.Equal("Mock", result.Provider);
        Assert.StartsWith("mock_", result.Reference);
        Assert.Null(result.Error);
    }
}
