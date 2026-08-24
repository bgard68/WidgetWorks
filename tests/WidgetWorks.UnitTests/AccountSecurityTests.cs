using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Auth;
using WidgetWorks.Application.Auth.Login;
using WidgetWorks.Application.Security.SecureAccount;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Users;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

public class AccountSecurityTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Account_locks_after_max_failed_attempts_then_unlocks_after_window()
    {
        var clock = new FakeTimeProvider(Start);
        var users = new InMemoryUserRepository();
        var refresh = new InMemoryRefreshTokenRepository();
        var hasher = new FakePasswordHasher();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "a@b.com",
            NormalizedEmail = "A@B.COM",
            PasswordHash = hasher.Hash("correct-horse"),
            SecurityStamp = Guid.NewGuid(),
        };
        users.Store[user.Id] = user;

        var options = new AccountSecurityOptions { MaxFailedAttempts = 3, LockoutMinutes = 15 };
        var handler = new LoginHandler(users, refresh, hasher, new StubTokenService(), new RecordingAuditLog(), options, clock);

        for (var i = 0; i < 3; i++)
        {
            var bad = await handler.Handle(new LoginCommand("a@b.com", "wrong"), CancellationToken.None);
            Assert.True(bad.IsFailure);
        }

        // Locked: even the correct password is refused now.
        var locked = await handler.Handle(new LoginCommand("a@b.com", "correct-horse"), CancellationToken.None);
        Assert.True(locked.IsFailure);
        Assert.True(user.IsLockedOut(clock.GetUtcNow()));

        // Advance past the lockout window -> login succeeds.
        clock.Advance(TimeSpan.FromMinutes(15));
        var ok = await handler.Handle(new LoginCommand("a@b.com", "correct-horse"), CancellationToken.None);
        Assert.True(ok.IsSuccess);
    }

    [Fact]
    public async Task SecureAccount_rotates_stamp_and_revokes_all_refresh_tokens()
    {
        var clock = new FakeTimeProvider(Start);
        var users = new InMemoryUserRepository();
        var refresh = new InMemoryRefreshTokenRepository();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "a@b.com",
            NormalizedEmail = "A@B.COM",
            SecurityStamp = Guid.NewGuid(),
        };
        users.Store[user.Id] = user;
        var originalStamp = user.SecurityStamp;

        refresh.Tokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = "h1",
            FamilyId = Guid.NewGuid(),
            ExpiresAt = clock.GetUtcNow().AddDays(1),
            CreatedAt = clock.GetUtcNow(),
        });

        var handler = new SecureAccountHandler(users, refresh, new RecordingAuditLog(), clock);
        var result = await handler.Handle(new SecureAccountCommand(user.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(originalStamp, users.Store[user.Id].SecurityStamp);   // stamp rotated -> access tokens invalid
        Assert.All(refresh.Tokens, t => Assert.NotNull(t.RevokedAt));          // every refresh token revoked
    }

    [Fact]
    public async Task SecureAccount_for_an_unknown_user_fails()
    {
        var handler = new SecureAccountHandler(
            new InMemoryUserRepository(), new InMemoryRefreshTokenRepository(), new RecordingAuditLog(), new FakeTimeProvider(Start));

        var result = await handler.Handle(new SecureAccountCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("User not found.", result.Error);
    }
}
