namespace WidgetWorks.Application.Abstractions;

public sealed record TotpSecret(string SecretBase32, string OtpAuthUri);

public interface ITotpService
{
    /// <summary>Generates a new TOTP secret and the otpauth:// URI to render as a QR code.</summary>
    TotpSecret CreateSecret(string accountName);

    /// <summary>Verifies a submitted 6-digit code against the secret at the given time (injected clock).</summary>
    bool Verify(string secretBase32, string code, DateTimeOffset now);
}
