using System.Security.Cryptography;
using System.Text;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Security;

/// <summary>Random opaque tokens (32 bytes, base64url) hashed with SHA-256 for at-rest storage.</summary>
public sealed class SecureTokenGenerator : ISecureTokenGenerator
{
    public string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public string Hash(string rawToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(hash);
    }
}
