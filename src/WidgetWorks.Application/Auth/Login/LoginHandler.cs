using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Auth.Login;

public sealed record LoginCommand(string Email, string Password);

public sealed class LoginHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher hasher,
    ITokenService tokens,
    TimeProvider clock)
{
    public async Task<Result<AuthResponse>> Handle(LoginCommand command, CancellationToken ct)
    {
        var normalized = (command.Email ?? string.Empty).Trim().ToUpperInvariant();
        var user = await users.GetByNormalizedEmailAsync(normalized, ct);
        var now = clock.GetUtcNow();

        if (user is null || user.PasswordHash is null || !hasher.Verify(command.Password, user.PasswordHash))
        {
            return Result<AuthResponse>.Fail("Invalid email or password.");
        }

        if (user.IsLockedOut(now))
        {
            return Result<AuthResponse>.Fail("Account is temporarily locked. Try again later.");
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

        return Result<AuthResponse>.Success(new AuthResponse(
            access.Value,
            access.ExpiresAt,
            refresh.Value,
            refresh.ExpiresAt,
            user.Role));
    }
}
