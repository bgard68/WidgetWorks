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

    private static GoogleLoginHandler Handler(FakeGoogleTokenValidator validator, InMemoryUserRepository users)
        => new(validator, users, new InMemoryRefreshTokenRepository(), new StubTokenService(), new RecordingAuditLog(), new FakeEmailSender(), Clock());

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
}
