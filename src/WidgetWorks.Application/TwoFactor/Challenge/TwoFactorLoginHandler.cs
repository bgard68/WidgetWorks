using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Auth;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.TwoFactor.Challenge;

public sealed record TwoFactorLoginCommand(string ChallengeToken, string Code);

public sealed class TwoFactorLoginHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    ITwoFactorRepository twoFactor,
    ITotpService totp,
    ITokenService tokens,
    IAuditLog audit,
    TimeProvider clock)
{
    public async Task<Result<AuthResponse>> Handle(TwoFactorLoginCommand command, CancellationToken ct)
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
        var secret = await twoFactor.GetSecretAsync(user.Id, ct);
        if (secret is null || !secret.IsConfirmed || !totp.Verify(secret.Secret, command.Code, now))
        {
            await audit.WriteAsync(user.Id, "2fa.failed", null, ct);
            return Result<AuthResponse>.Fail("Invalid authenticator code.");
        }

        var response = await IssueTokensAsync(user, now, ct);
        await audit.WriteAsync(user.Id, "2fa.success", null, ct);
        return Result<AuthResponse>.Success(response);
    }

    private async Task<AuthResponse> IssueTokensAsync(User user, DateTimeOffset now, CancellationToken ct)
    {
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

        return new AuthResponse(access.Value, access.ExpiresAt, refresh.Value, refresh.ExpiresAt, user.Role);
    }
}
