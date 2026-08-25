using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application;
using WidgetWorks.Application.Auth.PasswordReset;
using WidgetWorks.Domain.Auth;
using WidgetWorks.Domain.Users;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

public class PasswordResetTests
{
    private sealed record Ctx(
        FakeTimeProvider Clock,
        InMemoryUserRepository Users,
        InMemoryPasswordResetTokenRepository Tokens,
        InMemoryRefreshTokenRepository Refresh,
        FakeSecureTokenGenerator Gen,
        FakePasswordHasher Hasher,
        FakeEmailSender Email,
        User User);

    private static Ctx Setup(bool protectedAdmin = false)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var users = new InMemoryUserRepository();
        var hasher = new FakePasswordHasher();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "jane@example.com",
            NormalizedEmail = "JANE@EXAMPLE.COM",
            PasswordHash = hasher.Hash("old-password"),
            SecurityStamp = Guid.NewGuid(),
            IsProtectedAdmin = protectedAdmin,
        };
        users.Store[user.Id] = user;
        return new Ctx(clock, users, new InMemoryPasswordResetTokenRepository(), new InMemoryRefreshTokenRepository(),
            new FakeSecureTokenGenerator(), hasher, new FakeEmailSender(), user);
    }

    private static RequestPasswordResetHandler Request(Ctx c, ILogger<RequestPasswordResetHandler>? logger = null)
        => new(c.Users, c.Tokens, c.Gen, c.Email, new AppOptions(), c.Clock, logger ?? NullLogger<RequestPasswordResetHandler>.Instance);

    private static ResetPasswordHandler Reset(Ctx c)
        => new(c.Users, c.Tokens, c.Refresh, c.Gen, c.Hasher, new RecordingAuditLog(), c.Clock);

    [Fact]
    public async Task Request_creates_token_and_emails_link_for_existing_user()
    {
        var c = Setup();
        var result = await Request(c).Handle(new RequestPasswordResetCommand("jane@example.com"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(c.Tokens.Tokens);
        Assert.Contains(c.Email.Sent, m => m.Subject.Contains("Reset"));
    }

    [Fact]
    public async Task Request_for_unknown_email_is_silent_success()
    {
        var c = Setup();
        var result = await Request(c).Handle(new RequestPasswordResetCommand("nobody@example.com"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(c.Tokens.Tokens);
        Assert.Empty(c.Email.Sent);
    }

    [Fact]
    public async Task Reset_with_valid_token_changes_password_and_rotates_stamp()
    {
        var c = Setup();
        await Request(c).Handle(new RequestPasswordResetCommand("jane@example.com"), CancellationToken.None);
        var original = c.User.SecurityStamp;

        var result = await Reset(c).Handle(new ResetPasswordCommand(c.Gen.Last, "new-password"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(c.Hasher.Verify("new-password", c.Users.Store[c.User.Id].PasswordHash!));
        Assert.NotEqual(original, c.Users.Store[c.User.Id].SecurityStamp);
        Assert.NotNull(c.Tokens.Tokens[0].UsedAt);
    }

    [Fact]
    public async Task Reset_token_is_single_use()
    {
        var c = Setup();
        await Request(c).Handle(new RequestPasswordResetCommand("jane@example.com"), CancellationToken.None);
        await Reset(c).Handle(new ResetPasswordCommand(c.Gen.Last, "new-password"), CancellationToken.None);

        var again = await Reset(c).Handle(new ResetPasswordCommand(c.Gen.Last, "another-password"), CancellationToken.None);

        Assert.True(again.IsFailure);
    }

    [Fact]
    public async Task Reset_fails_after_expiry()
    {
        var c = Setup();
        await Request(c).Handle(new RequestPasswordResetCommand("jane@example.com"), CancellationToken.None);
        c.Clock.Advance(TimeSpan.FromMinutes(31));

        var result = await Reset(c).Handle(new ResetPasswordCommand(c.Gen.Last, "new-password"), CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Protected_admin_never_gets_a_reset_token()
    {
        var c = Setup(protectedAdmin: true);
        var result = await Request(c).Handle(new RequestPasswordResetCommand("jane@example.com"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(c.Tokens.Tokens);
        Assert.Empty(c.Email.Sent);
    }

    [Fact]
    public async Task Request_still_succeeds_when_the_reset_email_fails()
    {
        var c = Setup();
        var logger = new RecordingLogger<RequestPasswordResetHandler>();
        var handler = new RequestPasswordResetHandler(c.Users, c.Tokens, c.Gen, new ThrowingEmailSender(), new WidgetWorks.Application.AppOptions(), c.Clock, logger);

        var result = await handler.Handle(new RequestPasswordResetCommand("jane@example.com"), CancellationToken.None);

        // Still a silent 200: a mail outage must not become an account-enumeration oracle.
        Assert.True(result.IsSuccess);
        Assert.Single(c.Tokens.Tokens);

        // Silent to the caller, not to the operator - a reset nobody receives is
        // otherwise indistinguishable from one nobody requested.
        var logged = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.NotNull(logged.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    public async Task Reset_requires_eight_characters_of_password(string password)
    {
        var c = Setup();

        var result = await Reset(c).Handle(new ResetPasswordCommand("whatever", password), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Password must be at least 8 characters.", result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Reset_requires_a_token(string token)
    {
        var c = Setup();

        var result = await Reset(c).Handle(new ResetPasswordCommand(token, "long-enough-pw"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Invalid or expired reset link.", result.Error);
    }

    [Fact]
    public async Task Reset_refuses_a_token_whose_user_is_gone_or_protected()
    {
        // Defense in depth: request never issues these tokens, so plant them directly.
        var c = Setup(protectedAdmin: true);
        var now = c.Clock.GetUtcNow();
        c.Tokens.Tokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = c.User.Id,   // protected admin
            TokenHash = c.Gen.Hash("planted-admin"),
            ExpiresAt = now.AddMinutes(30),
            CreatedAt = now,
        });
        c.Tokens.Tokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),   // no such user
            TokenHash = c.Gen.Hash("planted-orphan"),
            ExpiresAt = now.AddMinutes(30),
            CreatedAt = now,
        });

        var admin = await Reset(c).Handle(new ResetPasswordCommand("planted-admin", "long-enough-pw"), CancellationToken.None);
        var orphan = await Reset(c).Handle(new ResetPasswordCommand("planted-orphan", "long-enough-pw"), CancellationToken.None);

        Assert.Equal("Invalid or expired reset link.", admin.Error);
        Assert.Equal("Invalid or expired reset link.", orphan.Error);
        Assert.Equal(c.Hasher.Hash("old-password"), c.Users.Store[c.User.Id].PasswordHash);   // unchanged
    }

    private sealed class ThrowingEmailSender : WidgetWorks.Application.Abstractions.IEmailSender
    {
        public Task SendAsync(WidgetWorks.Application.Abstractions.EmailMessage message, CancellationToken ct)
            => throw new InvalidOperationException("smtp is down");
    }
}
