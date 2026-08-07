using OtpNet;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Security;

public sealed class TotpService : ITotpService
{
    private const string Issuer = "WidgetWorks";

    public TotpSecret CreateSecret(string accountName)
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        var base32 = Base32Encoding.ToString(key);
        var label = Uri.EscapeDataString($"{Issuer}:{accountName}");
        var uri = $"otpauth://totp/{label}?secret={base32}&issuer={Uri.EscapeDataString(Issuer)}&digits=6&period=30";
        return new TotpSecret(base32, uri);
    }

    public bool Verify(string secretBase32, string code, DateTimeOffset now)
    {
        var key = Base32Encoding.ToBytes(secretBase32);
        var totp = new Totp(key);
        return totp.VerifyTotp(now.UtcDateTime, code, out _, VerificationWindow.RfcSpecifiedNetworkDelay);
    }
}
