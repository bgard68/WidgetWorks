using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Auth.Logout;
using WidgetWorks.Application.Auth.Refresh;
using WidgetWorks.Application.Auth.Register;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Users;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// Refresh-token rotation, logout, and registration. The rotation cases matter most: a refresh
/// token is single-use, so replaying one has to revoke the whole family rather than mint a token.
/// </summary>
public class AuthSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Ctx(
        FakeTimeProvider Clock,
        InMemoryUserRepository Users,
        InMemoryRefreshTokenRepository Refresh,
        StubTokenService Tokens,
        User User);

    private static Ctx Setup()
    {
        var users = new InMemoryUserRepository();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "jane@example.com",
            NormalizedEmail = "JANE@EXAMPLE.COM",
            PasswordHash = "hash:pw",
            Role = UserRoles.Customer,
            SecurityStamp = Guid.NewGuid(),
        };
        users.Store[user.Id] = user;
        return new Ctx(new FakeTimeProvider(Now), users, new InMemoryRefreshTokenRepository(), new StubTokenService(), user);
    }

    private static RefreshToken Issue(Ctx c, Guid familyId, DateTimeOffset? expiresAt = null, DateTimeOffset? revokedAt = null)
    {
        var token = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = c.User.Id,
            TokenHash = c.Tokens.HashRefreshToken("raw-token"),
            FamilyId = familyId,
            ExpiresAt = expiresAt ?? Now.AddDays(14),
            CreatedAt = Now.AddMinutes(-5),
            RevokedAt = revokedAt,
        };
        c.Refresh.Tokens.Add(token);
        return token;
    }

    private static RefreshHandler Refresh(Ctx c) => new(c.Users, c.Refresh, c.Tokens, c.Clock);

    private static LogoutHandler Logout(Ctx c) => new(c.Refresh, c.Tokens, c.Clock);

    // ---- refresh -------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refresh_requires_a_token(string raw)
    {
        var c = Setup();
        var result = await Refresh(c).Handle(new RefreshCommand(raw), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Refresh token is required.", result.Error);
    }

    [Fact]
    public async Task Refresh_rejects_a_token_it_has_never_seen()
    {
        var c = Setup();
        var result = await Refresh(c).Handle(new RefreshCommand("never-issued"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid refresh token.", result.Error);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_keeps_the_family()
    {
        var c = Setup();
        var family = Guid.NewGuid();
        var original = Issue(c, family);

        var result = await Refresh(c).Handle(new RefreshCommand("raw-token"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("access-token", result.Value!.AccessToken);
        Assert.Equal(UserRoles.Customer, result.Value.Role);

        // The presented token is spent, and points at what replaced it.
        Assert.Equal(Now, original.RevokedAt);
        Assert.NotNull(original.ReplacedBy);

        var replacement = Assert.Single(c.Refresh.Tokens, t => t.Id != original.Id);
        Assert.Equal(family, replacement.FamilyId);
        Assert.Equal(original.ReplacedBy, replacement.Id);
        Assert.Null(replacement.RevokedAt);
    }

    [Fact]
    public async Task Refresh_of_a_revoked_token_revokes_the_whole_family()
    {
        var c = Setup();
        var family = Guid.NewGuid();
        Issue(c, family, revokedAt: Now.AddMinutes(-1));
        var sibling = Issue(c, family);

        var result = await Refresh(c).Handle(new RefreshCommand("raw-token"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Refresh token no longer valid.", result.Error);

        // Reuse detection: a stolen token being replayed must not leave live siblings behind.
        Assert.Equal(Now, sibling.RevokedAt);
        Assert.All(c.Refresh.Tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task Refresh_of_an_expired_token_revokes_the_family_too()
    {
        var c = Setup();
        var family = Guid.NewGuid();
        Issue(c, family, expiresAt: Now.AddSeconds(-1));

        var result = await Refresh(c).Handle(new RefreshCommand("raw-token"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.All(c.Refresh.Tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task Refresh_fails_when_the_user_behind_the_token_is_gone()
    {
        var c = Setup();
        Issue(c, Guid.NewGuid());
        c.Users.Store.Clear();

        var result = await Refresh(c).Handle(new RefreshCommand("raw-token"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid refresh token.", result.Error);
    }

    // ---- logout --------------------------------------------------------------------------

    [Fact]
    public async Task Logout_revokes_the_presented_token()
    {
        var c = Setup();
        var token = Issue(c, Guid.NewGuid());

        var result = await Logout(c).Handle(new LogoutCommand("raw-token"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, token.RevokedAt);
    }

    [Fact]
    public async Task Logout_without_a_token_succeeds_quietly()
    {
        var c = Setup();
        var result = await Logout(c).Handle(new LogoutCommand("  "), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(c.Refresh.Tokens);
    }

    [Fact]
    public async Task Logout_of_an_unknown_token_succeeds_without_touching_anything()
    {
        var c = Setup();
        var token = Issue(c, Guid.NewGuid());

        var result = await Logout(c).Handle(new LogoutCommand("some-other-token"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(token.RevokedAt);
    }

    [Fact]
    public async Task Logout_twice_keeps_the_first_revocation_time()
    {
        var c = Setup();
        var revokedEarlier = Now.AddMinutes(-10);
        var token = Issue(c, Guid.NewGuid(), revokedAt: revokedEarlier);

        await Logout(c).Handle(new LogoutCommand("raw-token"), CancellationToken.None);

        Assert.Equal(revokedEarlier, token.RevokedAt);
    }

    // ---- registration --------------------------------------------------------------------

    private static RegisterHandler Register(Ctx c, IEmailSender email)
        => new(c.Users, new FakePasswordHasher(), email, c.Clock);

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Register_requires_an_email_address(string email)
    {
        var c = Setup();
        var result = await Register(c, new FakeEmailSender()).Handle(new RegisterCommand(email, "long-enough-pw"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("A valid email is required.", result.Error);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Register_requires_eight_characters_of_password(string? password)
    {
        var c = Setup();
        var result = await Register(c, new FakeEmailSender()).Handle(new RegisterCommand("new@example.com", password!), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Password must be at least 8 characters.", result.Error);
    }

    [Fact]
    public async Task Register_does_not_reveal_that_an_email_is_already_taken()
    {
        var c = Setup();
        var result = await Register(c, new FakeEmailSender())
            .Handle(new RegisterCommand(" Jane@Example.com ", "long-enough-pw"), CancellationToken.None);

        Assert.False(result.IsSuccess);

        // Deliberately generic: the message must not distinguish "taken" from any other failure.
        Assert.Equal("Unable to register with the provided details.", result.Error);
        Assert.DoesNotContain("exists", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_creates_a_customer_and_sends_a_welcome()
    {
        var c = Setup();
        var email = new FakeEmailSender();

        var result = await Register(c, email).Handle(new RegisterCommand(" New@Example.com ", "long-enough-pw"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var created = Assert.Single(c.Users.Store.Values, u => u.NormalizedEmail == "NEW@EXAMPLE.COM");
        Assert.Equal("New@Example.com", created.Email);
        Assert.Equal(UserRoles.Customer, created.Role);
        Assert.Equal("hash:long-enough-pw", created.PasswordHash);
        Assert.NotEqual(Guid.Empty, created.SecurityStamp);
        Assert.Equal(Now, created.CreatedAt);
        Assert.Single(email.Sent, m => m.To == "New@Example.com");
    }

    [Fact]
    public async Task Register_still_succeeds_when_the_welcome_email_fails()
    {
        var c = Setup();

        var result = await Register(c, new ThrowingEmailSender())
            .Handle(new RegisterCommand("new@example.com", "long-enough-pw"), CancellationToken.None);

        // A dead mail server must not cost someone their account.
        Assert.True(result.IsSuccess);
        Assert.Contains(c.Users.Store.Values, u => u.NormalizedEmail == "NEW@EXAMPLE.COM");
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken ct)
            => throw new InvalidOperationException("smtp is down");
    }
}
