using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.Auth.Sessions;

/// <summary>
/// The single place a new session is created. See <see cref="ISessionIssuer"/> for why it is one
/// place rather than five.
///
/// It lives in the application layer rather than infrastructure because it owns no technology: it
/// composes two ports that are already abstractions. The JWT signing and the SQL insert stay behind
/// <see cref="ITokenService"/> and <see cref="IRefreshTokenRepository"/>, which is what keeps this
/// testable without a database or a key.
/// </summary>
public sealed class SessionIssuer(ITokenService tokens, IRefreshTokenRepository refreshTokens) : ISessionIssuer
{
    public async Task<AuthResponse> IssueAsync(User user, DateTimeOffset now, CancellationToken ct)
    {
        var access = tokens.CreateAccessToken(user);
        var refresh = tokens.CreateRefreshToken(Guid.NewGuid());

        // Stored before the tokens are handed back, so a failure here cannot produce a refresh token
        // the caller believes in and the database has never heard of.
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
