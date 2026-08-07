using OtpNet;
using WidgetWorks.Infrastructure.Security;
using Xunit;

namespace WidgetWorks.UnitTests;

public class TotpTests
{
    [Fact]
    public void CreateSecret_produces_base32_and_otpauth_uri()
    {
        var totp = new TotpService();
        var secret = totp.CreateSecret("user@example.com");

        Assert.False(string.IsNullOrWhiteSpace(secret.SecretBase32));
        Assert.StartsWith("otpauth://totp/", secret.OtpAuthUri);
    }

    [Fact]
    public void Verify_accepts_current_code_and_rejects_after_window()
    {
        var totp = new TotpService();
        var secret = totp.CreateSecret("user@example.com");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Compute the expected code at 'now' directly with Otp.NET.
        var otp = new Totp(Base32Encoding.ToBytes(secret.SecretBase32));
        var code = otp.ComputeTotp(now.UtcDateTime);

        Assert.True(totp.Verify(secret.SecretBase32, code, now));
        Assert.False(totp.Verify(secret.SecretBase32, code, now.AddMinutes(5)));
    }
}
