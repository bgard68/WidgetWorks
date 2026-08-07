using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Domain.Users;

namespace WidgetWorks.Infrastructure.Security;

public sealed class JwtTokenService(IOptions<JwtOptions> options, JwtKeyRing keyRing, TimeProvider clock) : ITokenService
{
    private const string PurposeClaim = "purpose";
    private const string TwoFactorPurpose = "2fa";

    private readonly JwtOptions _options = options.Value;

    public AccessToken CreateAccessToken(User user)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = keyRing.SigningCredentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [ClaimTypes.Role] = user.Role,
                ["stamp"] = user.SecurityStamp.ToString(),
            },
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
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

    public string CreateChallengeToken(User user)
    {
        var now = clock.GetUtcNow();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(5).UtcDateTime,
            SigningCredentials = keyRing.SigningCredentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [PurposeClaim] = TwoFactorPurpose,
            },
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public async Task<Guid?> ValidateChallengeTokenAsync(string challengeToken)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _options.Issuer,
            ValidAudience = _options.Audience,
            IssuerSigningKeyResolver = (_, _, kid, _) => keyRing.ResolveKeys(kid),
        };

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(challengeToken, parameters);
        if (!result.IsValid)
        {
            return null;
        }

        if (!result.Claims.TryGetValue(PurposeClaim, out var purpose) || purpose?.ToString() != TwoFactorPurpose)
        {
            return null;
        }

        return result.Claims.TryGetValue(JwtRegisteredClaimNames.Sub, out var sub) && Guid.TryParse(sub?.ToString(), out var id)
            ? id
            : null;
    }
}
