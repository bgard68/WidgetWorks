using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Auth.Google;
using WidgetWorks.Domain.Users;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

public class GoogleLoginTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static GoogleLoginHandler Handler(FakeGoogleTokenValidator validator, InMemoryUserRepository users, IEmailSender? email = null)
        => new(validator, users, new InMemoryRefreshTokenRepository(), new StubTokenService(), new RecordingAuditLog(), email ?? new FakeEmailSender(), Clock());

    [Fact]
    public async Task New_google_user_is_provisioned_and_tokens_issued()
    {
        var users = new InMemoryUserRepository();
        var validator = new FakeGoogleTokenValidator { Result = new GoogleIdentity("google-123", "new@example.com", true, "New User") };

        var result = await Handler(validator, users).Handle(new GoogleLoginCommand("id-token"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(users.Store);
        var created = users.Store.Values.First();
        Assert.Equal("google-123", created.GoogleSub);
        Assert.Null(created.PasswordHash);
        Assert.Equal(UserRoles.Customer, created.Role);
    }

    [Fact]
    public async Task Existing_email_is_linked_to_google()
    {
        var users = new InMemoryUserRepository();
        var existing = new User
        {
            Id = Guid.NewGuid(),
            Email = "jane@example.com",
            NormalizedEmail = "JANE@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid(),
            Role = UserRoles.Customer,
        };
        users.Store[existing.Id] = existing;
        var validator = new FakeGoogleTokenValidator { Result = new GoogleIdentity("google-xyz", "jane@example.com", true, "Jane") };

        var result = await Handler(validator, users).Handle(new GoogleLoginCommand("id-token"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(users.Store);   // linked, not duplicated
        Assert.Equal("google-xyz", users.Store[existing.Id].GoogleSub);
    }

    [Fact]
    public async Task Existing_google_sub_signs_in_same_user()
    {
        var users = new InMemoryUserRepository();
        var existing = new User
        {
            Id = Guid.NewGuid(),
            Email = "jane@example.com",
            NormalizedEmail = "JANE@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid(),
            Role = UserRoles.Customer,
            GoogleSub = "google-xyz",
        };
        users.Store[existing.Id] = existing;
        var validator = new FakeGoogleTokenValidator { Result = new GoogleIdentity("google-xyz", "jane@example.com", true, "Jane") };

        var result = await Handler(validator, users).Handle(new GoogleLoginCommand("id-token"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(users.Store);
    }

    [Fact]
    public async Task Unverified_google_email_is_refused()
    {
        var users = new InMemoryUserRepository();
        var validator = new FakeGoogleTokenValidator { Result = new GoogleIdentity("google-1", "x@example.com", false, null) };

        var result = await Handler(validator, users).Handle(new GoogleLoginCommand("id-token"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Empty(users.Store);
    }

    [Fact]
    public async Task Invalid_token_is_refused()
    {
        var users = new InMemoryUserRepository();
        var validator = new FakeGoogleTokenValidator { Result = null };

        var result = await Handler(validator, users).Handle(new GoogleLoginCommand("bad"), CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task A_locked_out_account_cannot_slip_in_through_google()
    {
        var users = new InMemoryUserRepository();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "jane@example.com",
            NormalizedEmail = "JANE@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid(),
            GoogleSub = "google-123",
            LockedUntil = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero),   // an hour past Clock()
        };
        users.Store[user.Id] = user;
        var validator = new FakeGoogleTokenValidator { Result = new GoogleIdentity("google-123", "jane@example.com", true, "Jane") };

        var result = await Handler(validator, users).Handle(new GoogleLoginCommand("id-token"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Account is temporarily locked. Try again later.", result.Error);
    }

    [Fact]
    public async Task Signup_still_succeeds_when_the_welcome_email_fails()
    {
        var users = new InMemoryUserRepository();
        var validator = new FakeGoogleTokenValidator { Result = new GoogleIdentity("google-999", "new@example.com", true, "New User") };

        var result = await Handler(validator, users, new ThrowingEmailSender()).Handle(new GoogleLoginCommand("id-token"), CancellationToken.None);

        // A dead mail server must not cost someone their first sign-in.
        Assert.True(result.IsSuccess);
        Assert.Single(users.Store);

        // The response carries the whole session the SPA stores.
        var auth = result.Value!;
        Assert.False(string.IsNullOrEmpty(auth.AccessToken));
        Assert.False(string.IsNullOrEmpty(auth.RefreshToken));
        Assert.True(auth.AccessTokenExpiresAt > Clock().GetUtcNow());
        Assert.True(auth.RefreshTokenExpiresAt > Clock().GetUtcNow());
        Assert.Equal(UserRoles.Customer, auth.Role);
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken ct)
            => throw new InvalidOperationException("smtp is down");
    }
}
