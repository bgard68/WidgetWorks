using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Auth;
using WidgetWorks.Application.Auth.Login;
using WidgetWorks.Application.TwoFactor.Challenge;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Users;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

public class TwoFactorLoginTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Login_with_2fa_enabled_returns_challenge_not_tokens()
    {
        var clock = new FakeTimeProvider(Start);
        var users = new InMemoryUserRepository();
        var hasher = new FakePasswordHasher();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "a@b.com",
            NormalizedEmail = "A@B.COM",
            PasswordHash = hasher.Hash("pw-12345678"),
            SecurityStamp = Guid.NewGuid(),
            TwoFactorEnabled = true,
        };
        users.Store[user.Id] = user;

        var handler = new LoginHandler(
            users,
            new InMemoryRefreshTokenRepository(),
            hasher,
            new StubTokenService(),
            new RecordingAuditLog(),
            new AccountSecurityOptions(),
            clock);

        var result = await handler.Handle(new LoginCommand("a@b.com", "pw-12345678"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.RequiresTwoFactor);
        Assert.False(string.IsNullOrEmpty(result.Value.ChallengeToken));
        Assert.Null(result.Value.Tokens);
    }

    [Fact]
    public async Task TwoFactorLogin_valid_code_issues_tokens_wrong_code_fails()
    {
        var clock = new FakeTimeProvider(Start);
        var users = new InMemoryUserRepository();
        var refresh = new InMemoryRefreshTokenRepository();
        var twoFactor = new InMemoryTwoFactorRepository();
        var totp = new FakeTotpService { ValidCode = "654321" };
        var stubTokens = new StubTokenService();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "a@b.com",
            NormalizedEmail = "A@B.COM",
            SecurityStamp = Guid.NewGuid(),
            TwoFactorEnabled = true,
        };
        users.Store[user.Id] = user;
        twoFactor.Secrets[user.Id] = new TwoFactorSecret { UserId = user.Id, Secret = "SECRET", IsConfirmed = true };

        var challenge = stubTokens.CreateChallengeToken(user);
        var handler = new TwoFactorLoginHandler(users, refresh, twoFactor, totp, stubTokens, new RecordingAuditLog(), clock);

        var ok = await handler.Handle(new TwoFactorLoginCommand(challenge, "654321"), CancellationToken.None);
        Assert.True(ok.IsSuccess);
        Assert.False(string.IsNullOrEmpty(ok.Value!.AccessToken));

        var bad = await handler.Handle(new TwoFactorLoginCommand(challenge, "000000"), CancellationToken.None);
        Assert.True(bad.IsFailure);
    }

    [Fact]
    public async Task An_unparseable_challenge_is_refused()
    {
        var handler = new TwoFactorLoginHandler(
            new InMemoryUserRepository(), new InMemoryRefreshTokenRepository(), new InMemoryTwoFactorRepository(),
            new FakeTotpService(), new StubTokenService(), new RecordingAuditLog(), new FakeTimeProvider(Start));

        var result = await handler.Handle(new TwoFactorLoginCommand("garbage", "654321"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Invalid or expired challenge.", result.Error);
    }

    [Fact]
    public async Task A_challenge_for_a_missing_or_non_2fa_user_is_refused()
    {
        var users = new InMemoryUserRepository();
        var stubTokens = new StubTokenService();
        var plain = new User { Id = Guid.NewGuid(), Email = "a@b.com", NormalizedEmail = "A@B.COM", SecurityStamp = Guid.NewGuid(), TwoFactorEnabled = false };
        users.Store[plain.Id] = plain;
        var handler = new TwoFactorLoginHandler(
            users, new InMemoryRefreshTokenRepository(), new InMemoryTwoFactorRepository(),
            new FakeTotpService(), stubTokens, new RecordingAuditLog(), new FakeTimeProvider(Start));

        var gone = await handler.Handle(
            new TwoFactorLoginCommand(stubTokens.CreateChallengeToken(new User { Id = Guid.NewGuid() }), "654321"), CancellationToken.None);
        var non2fa = await handler.Handle(
            new TwoFactorLoginCommand(stubTokens.CreateChallengeToken(plain), "654321"), CancellationToken.None);

        Assert.Equal("Invalid challenge.", gone.Error);
        Assert.Equal("Invalid challenge.", non2fa.Error);
    }
}
