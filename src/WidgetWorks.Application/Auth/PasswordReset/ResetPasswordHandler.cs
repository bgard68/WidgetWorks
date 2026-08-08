using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Auth.PasswordReset;

public sealed record ResetPasswordCommand(string Token, string NewPassword);

/// <summary>
/// Completes a password reset: validates the single-use token, sets the new password, rotates the
/// security stamp and revokes all refresh tokens (logging every session out), and consumes the token.
/// </summary>
public sealed class ResetPasswordHandler(
    IUserRepository users,
    IPasswordResetTokenRepository tokens,
    IRefreshTokenRepository refreshTokens,
    ISecureTokenGenerator generator,
    IPasswordHasher passwordHasher,
    IAuditLog audit,
    TimeProvider clock)
{
    public async Task<Result> Handle(ResetPasswordCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.NewPassword) || command.NewPassword.Length < 8)
        {
            return Result.Fail("Password must be at least 8 characters.");
        }

        if (string.IsNullOrWhiteSpace(command.Token))
        {
            return Result.Fail("Invalid or expired reset link.");
        }

        var now = clock.GetUtcNow();
        var record = await tokens.GetByHashAsync(generator.Hash(command.Token.Trim()), ct);
        if (record is null || !record.IsActive(now))
        {
            return Result.Fail("Invalid or expired reset link.");
        }

        var user = await users.GetByIdAsync(record.UserId, ct);
        if (user is null || user.IsProtectedAdmin)
        {
            return Result.Fail("Invalid or expired reset link.");
        }

        user.PasswordHash = passwordHasher.Hash(command.NewPassword);
        user.SecurityStamp = Guid.NewGuid();   // invalidates existing access tokens
        user.FailedAccessCount = 0;
        user.LockedUntil = null;
        await users.UpdateAsync(user, ct);

        await tokens.MarkUsedAsync(record.Id, now, ct);
        await refreshTokens.RevokeAllForUserAsync(user.Id, now, ct);   // revoke every session
        await audit.WriteAsync(user.Id, "password.reset", null, ct);

        return Result.Success();
    }
}
