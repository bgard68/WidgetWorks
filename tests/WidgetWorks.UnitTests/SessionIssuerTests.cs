using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Auth.Sessions;
using WidgetWorks.Domain.Users;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The one place a new session is created, so the rules that used to be restated in five handlers
/// are asserted once here.
/// </summary>
public class SessionIssuerTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static User AUser() => new()
    {
        Id = Guid.NewGuid(),
        Email = "jane@example.com",
        NormalizedEmail = "JANE@EXAMPLE.COM",
        SecurityStamp = Guid.NewGuid(),
        Role = UserRoles.Customer,
    };

    /// <summary>
    /// Records exactly what it minted, so the test can check the stored row against it rather than
    /// against a second guess at how the hash is derived. <see cref="StubTokenService"/> cannot be
    /// used for this one: its <c>CreateRefreshToken</c> and <c>HashRefreshToken</c> do not agree
    /// with each other, so asserting through it would be testing the stub.
    /// </summary>
    private sealed class RecordingTokenService : ITokenService
    {
        private static readonly DateTimeOffset Far = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public IssuedRefreshToken? Issued { get; private set; }

        public AccessToken CreateAccessToken(User user) => new("access-token", Far);

        public IssuedRefreshToken CreateRefreshToken(Guid familyId)
            => Issued = new IssuedRefreshToken($"raw-{familyId}", $"hashed-{familyId}", familyId, Far);

        public string HashRefreshToken(string rawToken) => "hashed-" + rawToken["raw-".Length..];

        public string CreateChallengeToken(User user) => "challenge";

        public Task<Guid?> ValidateChallengeTokenAsync(string challengeToken) => Task.FromResult<Guid?>(null);
    }

    [Fact]
    public async Task The_refresh_token_handed_back_is_the_one_that_was_stored()
    {
        // The property that matters. Handing back a token the database never recorded would mean
        // the caller believes they are signed in and the very next refresh signs them out.
        var refresh = new InMemoryRefreshTokenRepository();
        var tokens = new RecordingTokenService();
        var user = AUser();

        var response = await new SessionIssuer(tokens, refresh).IssueAsync(user, Now, CancellationToken.None);

        var stored = Assert.Single(refresh.Tokens);
        Assert.Equal(user.Id, stored.UserId);
        Assert.Equal(tokens.Issued!.Value, response.RefreshToken);
        Assert.Equal(tokens.Issued.Hash, stored.TokenHash);
        Assert.Equal(tokens.Issued.FamilyId, stored.FamilyId);
    }

    [Fact]
    public async Task Each_session_starts_its_own_token_family()
    {
        // Every caller of this has just finished authenticating someone, so the sessions that came
        // before are unrelated. Sharing a family would let a stolen token from an old session be
        // detected as reuse against a new one, revoking a session that did nothing wrong.
        var refresh = new InMemoryRefreshTokenRepository();
        var issuer = new SessionIssuer(new StubTokenService(), refresh);
        var user = AUser();

        await issuer.IssueAsync(user, Now, CancellationToken.None);
        await issuer.IssueAsync(user, Now, CancellationToken.None);

        var families = refresh.Tokens.Select(t => t.FamilyId).Distinct().ToList();
        Assert.Equal(2, families.Count);
    }

    [Fact]
    public async Task The_response_carries_the_users_role()
    {
        // The client decides what to render from this, so a wrong role here is a staff menu shown
        // to a customer — or, more quietly, a staff member who cannot see their own tools.
        var refresh = new InMemoryRefreshTokenRepository();
        var user = AUser();
        user.Role = UserRoles.Manager;

        var response = await new SessionIssuer(new StubTokenService(), refresh).IssueAsync(user, Now, CancellationToken.None);

        Assert.Equal(UserRoles.Manager, response.Role);
    }

    [Fact]
    public async Task The_stored_token_is_stamped_with_the_supplied_time()
    {
        // Taken as a parameter rather than read from a clock inside, so the caller's notion of "now"
        // and the row's agree — the handlers already have one, and two sources would drift.
        var refresh = new InMemoryRefreshTokenRepository();

        await new SessionIssuer(new StubTokenService(), refresh).IssueAsync(AUser(), Now, CancellationToken.None);

        Assert.Equal(Now, Assert.Single(refresh.Tokens).CreatedAt);
    }
}
