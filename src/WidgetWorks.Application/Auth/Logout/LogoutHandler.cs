using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Common;

namespace WidgetWorks.Application.Auth.Logout;

public sealed record LogoutCommand(string RefreshToken);

public sealed class LogoutHandler(
    IRefreshTokenRepository refreshTokens,
    ITokenService tokens,
    TimeProvider clock)
{
    public async Task<Result> Handle(LogoutCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return Result.Success();
        }

        var hash = tokens.HashRefreshToken(command.RefreshToken);
        var existing = await refreshTokens.GetByHashAsync(hash, ct);
        if (existing is { RevokedAt: null })
        {
            existing.RevokedAt = clock.GetUtcNow();
            await refreshTokens.UpdateAsync(existing, ct);
        }

        return Result.Success();
    }
}
