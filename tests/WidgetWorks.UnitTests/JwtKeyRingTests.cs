using WidgetWorks.Infrastructure.Security;
using Xunit;

namespace WidgetWorks.UnitTests;

public class JwtKeyRingTests
{
    // 'test-signing-key' prefix keeps these throwaway values out of the gitleaks gate.
    private const string KeyOne = "test-signing-key-ring-one-0123456789";
    private const string KeyTwo = "test-signing-key-ring-two-0123456789";

    [Fact]
    public void Signs_with_active_key_and_validates_previous_keys()
    {
        var options = new JwtOptions
        {
            ActiveKeyId = "wk-2",
            Keys =
            {
                new JwtSigningKey { Kid = "wk-1", Secret = KeyOne },
                new JwtSigningKey { Kid = "wk-2", Secret = KeyTwo },
            },
        };

        var ring = new JwtKeyRing(options);

        Assert.Equal("wk-2", ring.SigningCredentials.Key.KeyId);
        Assert.Single(ring.ResolveKeys("wk-1"));   // previous key still trusted
        Assert.Single(ring.ResolveKeys("wk-2"));
        Assert.Empty(ring.ResolveKeys("unknown"));
    }

    [Fact]
    public void Revoked_key_is_rejected()
    {
        var options = new JwtOptions
        {
            ActiveKeyId = "wk-2",
            Keys =
            {
                new JwtSigningKey { Kid = "wk-1", Secret = KeyOne, Revoked = true },
                new JwtSigningKey { Kid = "wk-2", Secret = KeyTwo },
            },
        };

        var ring = new JwtKeyRing(options);

        Assert.Empty(ring.ResolveKeys("wk-1"));   // revoked -> no key -> token rejected
        Assert.Single(ring.ResolveKeys("wk-2"));
    }

    [Fact]
    public void Single_key_mode_falls_back_to_SigningKey()
    {
        var options = new JwtOptions { KeyId = "wk-1", SigningKey = KeyOne };

        var ring = new JwtKeyRing(options);

        Assert.Equal("wk-1", ring.SigningCredentials.Key.KeyId);
        Assert.Single(ring.ResolveKeys("wk-1"));
    }
}
