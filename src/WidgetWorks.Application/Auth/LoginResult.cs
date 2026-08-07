namespace WidgetWorks.Application.Auth;

/// <summary>
/// Result of a password login: either full tokens, or a 2FA challenge that must be
/// completed at /auth/2fa (or /auth/2fa/recovery) before tokens are issued.
/// </summary>
public sealed record LoginResult(bool RequiresTwoFactor, string? ChallengeToken, AuthResponse? Tokens);
