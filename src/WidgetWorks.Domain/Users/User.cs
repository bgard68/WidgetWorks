namespace WidgetWorks.Domain.Users;

public sealed class User
{
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string NormalizedEmail { get; set; } = string.Empty;

    public string? PasswordHash { get; set; }

    public string Role { get; set; } = UserRoles.Customer;

    public Guid SecurityStamp { get; set; }

    public bool IsProtectedAdmin { get; set; }

    public bool TwoFactorEnabled { get; set; }

    public string? GoogleSub { get; set; }

    public int FailedAccessCount { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;
}
