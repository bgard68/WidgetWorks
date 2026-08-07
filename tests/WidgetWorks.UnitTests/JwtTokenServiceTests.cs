using Microsoft.Extensions.Options;
using WidgetWorks.Domain.Users;
using WidgetWorks.Infrastructure.Security;
using Xunit;

namespace WidgetWorks.UnitTests;

public class JwtTokenServiceTests
{
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static JwtTokenService CreateService(DateTimeOffset now)
    {
        var jwtOptions = new JwtOptions
        {
            Issuer = "https://localhost",
            Audience = "widgetworks",
            SigningKey = "test-signing-key-that-is-long-enough-0123456789",
            KeyId = "wk-1",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 14,
        };
        var options = Options.Create(jwtOptions);
        return new JwtTokenService(options, new JwtKeyRing(jwtOptions), new FixedTimeProvider(now));
    }

    [Fact]
    public void CreateAccessToken_sets_expiry_from_clock()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = CreateService(now);

        var token = service.CreateAccessToken(new User
        {
            Id = Guid.NewGuid(),
            Role = UserRoles.Customer,
            SecurityStamp = Guid.NewGuid(),
        });

        Assert.False(string.IsNullOrWhiteSpace(token.Value));
        Assert.Equal(now.AddMinutes(15), token.ExpiresAt);
    }

    [Fact]
    public void HashRefreshToken_is_deterministic_and_distinct()
    {
        var service = CreateService(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(service.HashRefreshToken("abc"), service.HashRefreshToken("abc"));
        Assert.NotEqual(service.HashRefreshToken("abc"), service.HashRefreshToken("xyz"));
    }
}
