using Microsoft.Extensions.Options;
using WidgetWorks.Domain.Users;
using WidgetWorks.Infrastructure.Security;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The short-lived token that carries a half-authenticated user between "password accepted" and
/// "second factor proved". It is the one credential in the system that deliberately grants nothing
/// on its own, so the tests are mostly about what it must refuse: an access token presented in its
/// place, a token past its five minutes, one signed by a key the ring does not know, and one whose
/// signature has been tampered with.
/// </summary>
public class ChallengeTokenTests
{
    // Issuance uses the injected clock, but ValidateTokenAsync checks lifetime against the system
    // clock — TokenValidationParameters has no TimeProvider. Correct in production, where the two
    // agree; in tests it means a token minted at a fixed past date is already expired. So these
    // tests anchor on real time and move the *issuing* clock to express age.
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static JwtOptions Options(string key = "test-signing-key-that-is-long-enough-0123456789", string kid = "wk-1") => new()
    {
        Issuer = "https://localhost",
        Audience = "widgetworks",
        SigningKey = key,
        KeyId = kid,
        AccessTokenMinutes = 15,
        RefreshTokenDays = 14,
    };

    private static JwtTokenService Service(DateTimeOffset now, JwtOptions? options = null)
    {
        var o = options ?? Options();
        return new JwtTokenService(Microsoft.Extensions.Options.Options.Create(o), new JwtKeyRing(o), new FixedClock(now));
    }

    private static User TheUser { get; } = new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Email = "jane@example.com",
        NormalizedEmail = "JANE@EXAMPLE.COM",
        Role = UserRoles.Customer,
        SecurityStamp = Guid.NewGuid(),
    };

    [Fact]
    public async Task A_freshly_issued_challenge_resolves_to_its_user()
    {
        var service = Service(Now);

        var token = service.CreateChallengeToken(TheUser);

        Assert.Equal(TheUser.Id, await service.ValidateChallengeTokenAsync(token));
    }

    [Fact]
    public async Task An_access_token_is_not_accepted_as_a_challenge()
    {
        var service = Service(Now);

        // Both are signed by the same key and would validate structurally; only the purpose claim
        // separates them. Without that check, a full access token would satisfy the 2FA step.
        var access = service.CreateAccessToken(TheUser);

        Assert.Null(await service.ValidateChallengeTokenAsync(access.Value));
    }

    [Fact]
    public async Task A_challenge_still_works_a_few_minutes_in()
    {
        var issuedFourMinutesAgo = Service(Now.AddMinutes(-4)).CreateChallengeToken(TheUser);

        Assert.Equal(TheUser.Id, await Service(Now).ValidateChallengeTokenAsync(issuedFourMinutesAgo));
    }

    [Fact]
    public async Task A_challenge_older_than_five_minutes_is_refused()
    {
        var stale = Service(Now.AddMinutes(-30)).CreateChallengeToken(TheUser);

        // A half-authenticated session must not stay open indefinitely.
        Assert.Null(await Service(Now).ValidateChallengeTokenAsync(stale));
    }

    [Fact]
    public async Task A_challenge_signed_by_an_unknown_key_is_rejected()
    {
        var foreign = Service(Now, Options(key: "a-completely-different-signing-key-9876543210", kid: "other"))
            .CreateChallengeToken(TheUser);

        Assert.Null(await Service(Now).ValidateChallengeTokenAsync(foreign));
    }

    [Fact]
    public async Task A_tampered_challenge_is_rejected()
    {
        var service = Service(Now);
        var token = service.CreateChallengeToken(TheUser);

        // Flip the last character of the signature segment.
        var parts = token.Split('.');
        var signature = parts[2];
        parts[2] = signature[..^1] + (signature[^1] == 'A' ? 'B' : 'A');

        Assert.Null(await service.ValidateChallengeTokenAsync(string.Join('.', parts)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public async Task Garbage_is_rejected_without_throwing(string token)
    {
        Assert.Null(await Service(Now).ValidateChallengeTokenAsync(token));
    }

    [Fact]
    public async Task A_challenge_for_a_different_audience_is_rejected()
    {
        var options = Options();
        options.Audience = "some-other-app";
        var foreign = Service(Now, options).CreateChallengeToken(TheUser);

        Assert.Null(await Service(Now).ValidateChallengeTokenAsync(foreign));
    }

    [Fact]
    public async Task A_challenge_from_a_different_issuer_is_rejected()
    {
        var options = Options();
        options.Issuer = "https://not-us";
        var foreign = Service(Now, options).CreateChallengeToken(TheUser);

        Assert.Null(await Service(Now).ValidateChallengeTokenAsync(foreign));
    }

    [Fact]
    public void A_refresh_token_is_random_each_time_and_hashes_to_its_own_value()
    {
        var service = Service(Now);
        var family = Guid.NewGuid();

        var first = service.CreateRefreshToken(family);
        var second = service.CreateRefreshToken(family);

        Assert.NotEqual(first.Value, second.Value);
        Assert.Equal(family, first.FamilyId);
        Assert.Equal(service.HashRefreshToken(first.Value), first.Hash);
        Assert.NotEqual(first.Hash, second.Hash);

        // 14 days from the injected clock.
        Assert.Equal(14, Math.Round((first.ExpiresAt - Now).TotalDays));
    }
}
