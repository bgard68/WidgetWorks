using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace WidgetWorks.Infrastructure.Security;

/// <summary>
/// kid-based signing-key ring: signs with the active key, validates tokens signed by any
/// still-trusted key, and rejects tokens whose kid is unknown or revoked.
/// </summary>
public sealed class JwtKeyRing
{
    private readonly Dictionary<string, SecurityKey> _validationKeys;
    private readonly SigningCredentials _signingCredentials;

    public JwtKeyRing(JwtOptions options)
    {
        var keys = options.Keys.Count > 0
            ? options.Keys
            : [new JwtSigningKey { Kid = options.KeyId, Secret = options.SigningKey }];

        _validationKeys = keys
            .Where(k => !k.Revoked && !string.IsNullOrWhiteSpace(k.Secret))
            .ToDictionary(
                k => k.Kid,
                k => (SecurityKey)new SymmetricSecurityKey(Encoding.UTF8.GetBytes(k.Secret)) { KeyId = k.Kid });

        var activeKid = options.ActiveKeyId
            ?? keys.FirstOrDefault(k => !k.Revoked)?.Kid
            ?? options.KeyId;
        var active = keys.First(k => k.Kid == activeKid);
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(active.Secret)) { KeyId = active.Kid };
        _signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
    }

    public SigningCredentials SigningCredentials => _signingCredentials;

    /// <summary>Resolves validation key(s) for a token's kid. Unknown/revoked kid -> none (token rejected).</summary>
    public IEnumerable<SecurityKey> ResolveKeys(string? kid)
    {
        if (kid is not null)
        {
            return _validationKeys.TryGetValue(kid, out var key) ? [key] : [];
        }

        return _validationKeys.Values;
    }
}
