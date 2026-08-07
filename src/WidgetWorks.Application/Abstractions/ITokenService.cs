using WidgetWorks.Domain.Users;

namespace WidgetWorks.Application.Abstractions;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public sealed record IssuedRefreshToken(string Value, string Hash, Guid FamilyId, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user);

    IssuedRefreshToken CreateRefreshToken(Guid familyId);

    string HashRefreshToken(string rawToken);
}
