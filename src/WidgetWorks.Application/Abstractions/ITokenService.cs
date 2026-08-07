using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.Abstractions;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public sealed record IssuedRefreshToken(string Value, string Hash, Guid FamilyId, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user);

    IssuedRefreshToken CreateRefreshToken(Guid familyId);

    string HashRefreshToken(string rawToken);

    /// <summary>Creates a short-lived, limited-scope token that only authorizes the 2FA step.</summary>
    string CreateChallengeToken(User user);

    /// <summary>Validates a 2FA challenge token; returns the user id if valid, else null.</summary>
    Task<Guid?> ValidateChallengeTokenAsync(string challengeToken);
}
