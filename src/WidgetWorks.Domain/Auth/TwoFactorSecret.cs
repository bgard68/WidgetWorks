namespace WidgetWorks.Domain.Auth;

public sealed class TwoFactorSecret
{
    public Guid UserId { get; set; }

    public string Secret { get; set; } = string.Empty;

    public bool IsConfirmed { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
