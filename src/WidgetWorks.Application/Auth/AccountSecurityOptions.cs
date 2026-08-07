namespace WidgetWorks.Application.Auth;

public sealed class AccountSecurityOptions
{
    /// <summary>Failed login attempts allowed before the account is locked.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>How long the account stays locked once the threshold is hit.</summary>
    public int LockoutMinutes { get; set; } = 15;
}
