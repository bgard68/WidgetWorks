using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Common;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.Auth.Login;

public sealed record LoginCommand(string Email, string Password);

public sealed class LoginHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher hasher,
    ITokenService tokens,
    IAuditLog audit,
    AccountSecurityOptions security,
    TimeProvider clock)
{
    public async Task<Result<LoginResult>> Handle(LoginCommand command, CancellationToken ct)
    {
        var normalized = (command.Email ?? string.Empty).Trim().ToUpperInvariant();
        var user = await users.GetByNormalizedEmailAsync(normalized, ct);
        var now = clock.GetUtcNow();

        if (user is not null && user.IsLockedOut(now))
        {
            await audit.WriteAsync(user.Id, "login.locked", null, ct);
            return Result<LoginResult>.Fail("Account is temporarily locked. Try again later.");
        }

        if (user is null || user.PasswordHash is null || !hasher.Verify(command.Password, user.PasswordHash))
        {
            if (user is not null)
            {
                user.FailedAccessCount++;
                if (user.FailedAccessCount >= security.MaxFailedAttempts)
                {
                    user.LockedUntil = now.AddMinutes(security.LockoutMinutes);
                    user.FailedAccessCount = 0;
                    await users.UpdateAsync(user, ct);
                    await audit.WriteAsync(user.Id, "login.lockout", $"locked for {security.LockoutMinutes} minutes", ct);
                }
                else
                {
                    await users.UpdateAsync(user, ct);
                    await audit.WriteAsync(user.Id, "login.failed", null, ct);
                }
            }

            return Result<LoginResult>.Fail("Invalid email or password.");
        }

        if (user.FailedAccessCount != 0 || user.LockedUntil is not null)
        {
            user.FailedAccessCount = 0;
            user.LockedUntil = null;
            await users.UpdateAsync(user, ct);
        }

        if (user.TwoFactorEnabled)
        {
            var challenge = tokens.CreateChallengeToken(user);
            await audit.WriteAsync(user.Id, "login.2fa_challenge", null, ct);
            return Result<LoginResult>.Success(new LoginResult(true, challenge, null));
        }

        var response = await IssueTokensAsync(user, now, ct);
        await audit.WriteAsync(user.Id, "login.success", null, ct);
        return Result<LoginResult>.Success(new LoginResult(false, null, response));
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
