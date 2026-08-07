namespace WidgetWorks.Infrastructure.Security;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public string SigningKey { get; set; } = string.Empty;

    public string KeyId { get; set; } = "wk-1";

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>Which key id in <see cref="Keys"/> signs new tokens. Falls back to SigningKey/KeyId when Keys is empty.</summary>
    public string? ActiveKeyId { get; set; }

    /// <summary>Signing-key ring for kid-based rotation. Empty = single-key mode using SigningKey/KeyId.</summary>
    public List<JwtSigningKey> Keys { get; set; } = new();
}

public sealed class JwtSigningKey
{
    public string Kid { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;

    public bool Revoked { get; set; }
}
