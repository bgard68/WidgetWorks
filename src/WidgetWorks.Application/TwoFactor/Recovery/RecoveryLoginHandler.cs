using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Auth;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.TwoFactor.Recovery;

public sealed record RecoveryLoginCommand(string ChallengeToken, string RecoveryCode);

public sealed class RecoveryLoginHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    ITwoFactorRepository twoFactor,
    IRecoveryCodes recovery,
    ITokenService tokens,
    IAuditLog audit,
    TimeProvider clock)
{
    public async Task<Result<AuthResponse>> Handle(RecoveryLoginCommand command, CancellationToken ct)
    {
        var userId = await tokens.ValidateChallengeTokenAsync(command.ChallengeToken);
        if (userId is null)
        {
            return Result<AuthResponse>.Fail("Invalid or expired challenge.");
        }

        var user = await users.GetByIdAsync(userId.Value, ct);
        if (user is null || !user.TwoFactorEnabled)
        {
            return Result<AuthResponse>.Fail("Invalid challenge.");
        }

        var now = clock.GetUtcNow();
        var hash = recovery.Hash((command.RecoveryCode ?? string.Empty).Trim().ToLowerInvariant());
        var consumed = await twoFactor.ConsumeRecoveryCodeAsync(user.Id, hash, now, ct);
        if (!consumed)
        {
            await audit.WriteAsync(user.Id, "2fa.recovery_failed", null, ct);
            return Result<AuthResponse>.Fail("Invalid recovery code.");
        }

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

        await audit.WriteAsync(user.Id, "2fa.recovery_success", null, ct);
        return Result<AuthResponse>.Success(new AuthResponse(
            access.Value, access.ExpiresAt, refresh.Value, refresh.ExpiresAt, user.Role));
    }
}
