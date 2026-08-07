using WidgetWorks.Infrastructure.Security;
using Xunit;

namespace WidgetWorks.UnitTests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_then_verify_round_trips()
    {
        var hasher = new BcryptPasswordHasher();
        var hash = hasher.Hash("Sup3r!Secret");

        Assert.True(hasher.Verify("Sup3r!Secret", hash));
        Assert.False(hasher.Verify("wrong-password", hash));
    }
}
