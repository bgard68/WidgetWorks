using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Auth;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.Account.ChangePassword;

public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword);

/// <summary>
/// Changes the password of someone already signed in.
///
/// The reset-by-email flow existed; this did not, which meant the only way to change a password you
/// still knew was to pretend you had forgotten it and wait for a link.
///
/// Three things make it safe, and the third is the one that is usually missed:
///
/// 1. The current password is required. A signed-in session is not proof that the person at the
///    keyboard is the account holder — a borrowed laptop is the ordinary case — and without this
///    check an unlocked screen is a permanent account takeover.
/// 2. The new password goes through the same <see cref="PasswordPolicy"/> as registration and reset,
///    so this cannot become the soft way to set a weak one.
/// 3. Every other session is destroyed. Rotating the security stamp invalidates outstanding access
///    tokens and revoking the refresh tokens ends the sessions behind them — because the reason
///    people change a password is usually that they think someone else has it.
///
/// That last step would log the caller out too, so fresh tokens are issued for this session and
/// returned. The alternative — signing someone out of the page they just used successfully — reads
/// as a failure.
/// </summary>
public sealed class ChangePasswordHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher hasher,
    ITokenService tokens,
    IAuditLog audit,
    TimeProvider clock)
{
    public async Task<Result<AuthResponse>> Handle(ChangePasswordCommand command, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(command.UserId, ct);
        if (user is null)
        {
            return Result<AuthResponse>.Fail("User not found.");
        }

        if (user.PasswordHash is null)
        {
            // A Google-only account has no password to change, and setting one here would quietly
            // create a second way into the account.
            return Result<AuthResponse>.Fail("This account signs in with Google and has no password.");
        }

        if (!hasher.Verify(command.CurrentPassword ?? string.Empty, user.PasswordHash))
        {
            // Deliberately not counted toward lockout. The caller is already authenticated, so this is
            // a typo far more often than an attack, and locking someone out of their own account for
            // mistyping their current password is a worse outcome than the one it prevents.
            await audit.WriteAsync(user.Id, "password.change_refused", "current password did not match", ct);
            return Result<AuthResponse>.Fail("That is not your current password.");
        }

        if (!PasswordPolicy.IsAcceptable(command.NewPassword))
        {
            return Result<AuthResponse>.Fail(PasswordPolicy.Describe(command.NewPassword));
        }

        if (hasher.Verify(command.NewPassword, user.PasswordHash))
        {
            // Not a security rule, a courtesy: silently "succeeding" at changing nothing would leave
            // someone believing they had locked an intruder out.
            return Result<AuthResponse>.Fail("That is already your password. Choose a different one.");
        }

        var now = clock.GetUtcNow();

        user.PasswordHash = hasher.Hash(command.NewPassword);
        user.SecurityStamp = Guid.NewGuid();   // every outstanding access token stops validating
        await users.UpdateAsync(user, ct);

        await refreshTokens.RevokeAllForUserAsync(user.Id, now, ct);
        await audit.WriteAsync(user.Id, "password.changed", "all other sessions signed out", ct);

        // A new session for the caller, created after the revoke so it survives it.
        var access = tokens.CreateAccessToken(user);
        var refresh = tokens.CreateRefreshToken(Guid.NewGuid());
        await refreshTokens.AddAsync(
            new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = refresh.Hash,
                FamilyId = refresh.FamilyId,
                ExpiresAt = refresh.ExpiresAt,
                CreatedAt = now,
            },
            ct);

        return Result<AuthResponse>.Success(
            new AuthResponse(access.Value, access.ExpiresAt, refresh.Value, refresh.ExpiresAt, user.Role));
    }
}
