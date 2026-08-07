using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Security.SecureAccount;

public sealed record SecureAccountCommand(Guid UserId);

/// <summary>
/// Compromise response: rotates the user's security stamp (instantly invalidating every
/// outstanding access token via the OnTokenValidated check) and revokes all refresh tokens.
/// </summary>
public sealed class SecureAccountHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IAuditLog audit,
    TimeProvider clock)
{
    public async Task<Result> Handle(SecureAccountCommand command, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(command.UserId, ct);
        if (user is null)
        {
            return Result.Fail("User not found.");
        }

        var now = clock.GetUtcNow();
        user.SecurityStamp = Guid.NewGuid();   // rotate -> every existing access token fails validation
        user.FailedAccessCount = 0;
        user.LockedUntil = null;
        await users.UpdateAsync(user, ct);

        await refreshTokens.RevokeAllForUserAsync(user.Id, now, ct);   // kill every refresh session
        await audit.WriteAsync(user.Id, "account.secured", "security stamp rotated; refresh tokens revoked", ct);
        return Result.Success();
    }
}
