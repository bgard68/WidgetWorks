namespace WidgetWorks.Domain.Auth;

/// <summary>A single-use, time-limited password reset token. Only the SHA-256 hash is stored.</summary>
public sealed class PasswordResetToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;
}
