using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Infrastructure.Security;

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public AccessToken CreateAccessToken(User user)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)) { KeyId = _options.KeyId };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [ClaimTypes.Role] = user.Role,
                ["stamp"] = user.SecurityStamp.ToString(),
            },
        };

        var handler = new JsonWebTokenHandler();
        var token = handler.CreateToken(descriptor);
        return new AccessToken(token, expires);
    }

    public IssuedRefreshToken CreateRefreshToken(Guid familyId)
    {
        var now = clock.GetUtcNow();
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new IssuedRefreshToken(raw, HashRefreshToken(raw), familyId, now.AddDays(_options.RefreshTokenDays));
    }

    public string HashRefreshToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes);
    }
}
