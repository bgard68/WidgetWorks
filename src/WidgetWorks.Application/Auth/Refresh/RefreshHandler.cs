using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Auth.Refresh;

public sealed record RefreshCommand(string RefreshToken);

public sealed class RefreshHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    ITokenService tokens,
    TimeProvider clock)
{
    public async Task<Result<AuthResponse>> Handle(RefreshCommand command, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return Result<AuthResponse>.Fail("Refresh token is required.");
        }

        var hash = tokens.HashRefreshToken(command.RefreshToken);
        var existing = await refreshTokens.GetByHashAsync(hash, ct);
        if (existing is null)
        {
            return Result<AuthResponse>.Fail("Invalid refresh token.");
        }

        // Reuse detection: a revoked/expired token presented again revokes the whole family.
        if (!existing.IsActive(now))
        {
            await refreshTokens.RevokeFamilyAsync(existing.FamilyId, now, ct);
            return Result<AuthResponse>.Fail("Refresh token no longer valid.");
        }

        var user = await users.GetByIdAsync(existing.UserId, ct);
        if (user is null)
        {
            return Result<AuthResponse>.Fail("Invalid refresh token.");
        }

        // Rotate: revoke current and issue a new token in the same family.
        var next = tokens.CreateRefreshToken(existing.FamilyId);
        var replacement = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = next.Hash,
            FamilyId = existing.FamilyId,
            ExpiresAt = next.ExpiresAt,
            CreatedAt = now,
        };

        existing.RevokedAt = now;
        existing.ReplacedBy = replacement.Id;
        await refreshTokens.AddAsync(replacement, ct);
        await refreshTokens.UpdateAsync(existing, ct);

        var access = tokens.CreateAccessToken(user);
        return Result<AuthResponse>.Success(new AuthResponse(
            access.Value,
            access.ExpiresAt,
            next.Value,
            next.ExpiresAt,
            user.Role));
    }
}
