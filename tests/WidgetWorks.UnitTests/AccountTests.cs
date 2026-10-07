using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Account.ChangePassword;
using WidgetWorks.Application.Account.Profile;
using WidgetWorks.Domain.Users;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// The account area's own use cases: the profile, and changing a password you already know.
///
/// The password change is the one worth care. It is the only path that can set a password from
/// inside a session, so it is also the only one that could quietly become the soft way round the
/// rules the other two paths enforce.
/// </summary>
public class AccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const string Current = "Str0ng!Passw0rd";
    private const string Next = "An0ther!Passw0rd";

    private sealed record Ctx(
        InMemoryUserRepository Users,
        InMemoryRefreshTokenRepository RefreshTokens,
        RecordingAuditLog Audit,
        User User);

    private static Ctx Setup(bool withPassword = true)
    {
        var users = new InMemoryUserRepository();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "jane@example.com",
            NormalizedEmail = "JANE@EXAMPLE.COM",
            PasswordHash = withPassword ? "hash:" + Current : null,
            Role = UserRoles.Customer,
            SecurityStamp = Guid.NewGuid(),
            CreatedAt = Now.AddYears(-2),
        };
        users.Store[user.Id] = user;

        return new Ctx(users, new InMemoryRefreshTokenRepository(), new RecordingAuditLog(), user);
    }

    private static ChangePasswordHandler Handler(Ctx c)
        => new(c.Users, c.RefreshTokens, new FakePasswordHasher(), new StubTokenService(), c.Audit, new FakeTimeProvider(Now));

    [Fact]
    public async Task Changing_a_password_signs_every_other_session_out_but_not_this_one()
    {
        var c = Setup();
        var stampBefore = c.User.SecurityStamp;

        // A session already open on another device.
        var elsewhere = new WidgetWorks.Domain.Auth.RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = c.User.Id,
            TokenHash = "other-device",
            FamilyId = Guid.NewGuid(),
            ExpiresAt = Now.AddDays(30),
            CreatedAt = Now,
        };
        await c.RefreshTokens.AddAsync(elsewhere, CancellationToken.None);

        var result = await Handler(c).Handle(new ChangePasswordCommand(c.User.Id, Current, Next), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("hash:" + Next, c.User.PasswordHash);

        // Rotating the stamp invalidates every outstanding access token, and revoking kills the
        // refresh sessions behind them — because the reason people change a password is that they
        // think someone else has it.
        Assert.NotEqual(stampBefore, c.User.SecurityStamp);
        Assert.NotNull(elsewhere.RevokedAt);

        // The caller's own replacement was issued after the revoke, so it survives it.
        Assert.Contains(c.RefreshTokens.Tokens, t => t.RevokedAt is null && t.Id != elsewhere.Id);

        // And a fresh pair comes back, so the page that just succeeded does not appear to log you out.
        Assert.False(string.IsNullOrEmpty(result.Value!.AccessToken));
        Assert.False(string.IsNullOrEmpty(result.Value!.RefreshToken));
        Assert.Contains("password.changed", c.Audit.Actions);
    }

    [Fact]
    public async Task The_current_password_is_required_even_though_the_caller_is_signed_in()
    {
        var c = Setup();

        var result = await Handler(c).Handle(new ChangePasswordCommand(c.User.Id, "Wr0ng!Passw0rd", Next), CancellationToken.None);

        // A session is not proof of identity — a borrowed laptop is the ordinary case. Without this
        // check an unlocked screen is a permanent account takeover.
        Assert.True(result.IsFailure);
        Assert.Equal("That is not your current password.", result.Error);
        Assert.Equal("hash:" + Current, c.User.PasswordHash);
        Assert.Empty(c.RefreshTokens.Tokens);
        Assert.Contains("password.change_refused", c.Audit.Actions);
    }

    [Fact]
    public async Task A_wrong_current_password_does_not_count_toward_lockout()
    {
        var c = Setup();

        await Handler(c).Handle(new ChangePasswordCommand(c.User.Id, "Wr0ng!Passw0rd", Next), CancellationToken.None);

        // Deliberate. The caller is already authenticated, so this is a typo far more often than an
        // attack, and locking someone out of their own account for mistyping is the worse outcome.
        Assert.Equal(0, c.User.FailedAccessCount);
        Assert.Null(c.User.LockedUntil);
    }

    [Fact]
    public async Task The_new_password_faces_the_same_policy_as_registration()
    {
        var c = Setup();

        var result = await Handler(c).Handle(new ChangePasswordCommand(c.User.Id, Current, "weak"), CancellationToken.None);

        // Otherwise this becomes the soft way to set a password the other two paths would refuse.
        Assert.True(result.IsFailure);
        Assert.StartsWith("Password needs:", result.Error);
        Assert.Equal("hash:" + Current, c.User.PasswordHash);
    }

    [Fact]
    public async Task Re_setting_the_same_password_is_refused_rather_than_silently_accepted()
    {
        var c = Setup();

        var result = await Handler(c).Handle(new ChangePasswordCommand(c.User.Id, Current, Current), CancellationToken.None);

        // "Succeeding" at changing nothing would leave someone believing they had locked an intruder
        // out. Not a security rule — a courtesy, and the kind that matters.
        Assert.True(result.IsFailure);
        Assert.Contains("already your password", result.Error);
    }

    [Fact]
    public async Task A_google_only_account_has_no_password_to_change()
    {
        var c = Setup(withPassword: false);

        var result = await Handler(c).Handle(new ChangePasswordCommand(c.User.Id, "anything", Next), CancellationToken.None);

        // Setting one here would quietly create a second way into the account.
        Assert.True(result.IsFailure);
        Assert.Contains("signs in with Google", result.Error);
        Assert.Null(c.User.PasswordHash);
    }

    [Fact]
    public async Task An_unknown_user_cannot_change_a_password()
    {
        var c = Setup();

        var result = await Handler(c).Handle(new ChangePasswordCommand(Guid.NewGuid(), Current, Next), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("User not found.", result.Error);
    }

    [Fact]
    public async Task The_profile_reports_what_the_account_page_needs_and_nothing_more()
    {
        var c = Setup();

        var result = await new GetProfileHandler(c.Users).Handle(new GetProfileQuery(c.User.Id), CancellationToken.None);

        var profile = result.Value!;
        Assert.Equal("jane@example.com", profile.Email);
        Assert.Null(profile.DisplayName);
        Assert.Equal(Now.AddYears(-2), profile.MemberSince);

        // HasPassword decides whether the page offers a password change at all — a disabled button
        // with no explanation is worse than an absent one.
        Assert.True(profile.HasPassword);
    }

    [Fact]
    public async Task An_unknown_user_has_no_profile()
    {
        var c = Setup();

        var result = await new GetProfileHandler(c.Users).Handle(new GetProfileQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Theory]
    [InlineData("  Jane Doe  ", "Jane Doe")]   // trimmed
    [InlineData("Jane", "Jane")]
    public async Task A_name_is_stored_trimmed(string input, string expected)
    {
        var c = Setup();

        var result = await new UpdateProfileHandler(c.Users, c.Audit)
            .Handle(new UpdateProfileCommand(c.User.Id, input), CancellationToken.None);

        Assert.Equal(expected, result.Value!.DisplayName);
        Assert.Contains("profile.name_set", c.Audit.Actions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_means_no_name_rather_than_an_empty_one(string? input)
    {
        var c = Setup();
        c.User.DisplayName = "Jane";

        var result = await new UpdateProfileHandler(c.Users, c.Audit)
            .Handle(new UpdateProfileCommand(c.User.Id, input), CancellationToken.None);

        // Null, not "". The greeting falls back to a neutral one instead of rendering "Hello, " with
        // nothing after it.
        Assert.Null(result.Value!.DisplayName);
        Assert.Contains("profile.name_cleared", c.Audit.Actions);
    }

    [Fact]
    public async Task A_name_longer_than_the_limit_is_refused()
    {
        var c = Setup();

        var result = await new UpdateProfileHandler(c.Users, c.Audit)
            .Handle(new UpdateProfileCommand(c.User.Id, new string('a', UpdateProfileHandler.MaxLength + 1)), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Contains($"{UpdateProfileHandler.MaxLength} characters", result.Error);
    }

    [Fact]
    public async Task The_audit_trail_records_that_a_name_changed_but_not_to_what()
    {
        var c = Setup();

        await new UpdateProfileHandler(c.Users, c.Audit)
            .Handle(new UpdateProfileCommand(c.User.Id, "Jane Doe"), CancellationToken.None);

        // Who changed their own name is worth knowing; keeping a second copy of it in the audit
        // trail is not.
        var entry = Assert.Single(c.Audit.Entries, e => e.Action == "profile.name_set");
        Assert.Null(entry.Detail);
    }

    [Fact]
    public async Task An_unknown_user_cannot_be_given_a_name()
    {
        var c = Setup();

        var result = await new UpdateProfileHandler(c.Users, c.Audit)
            .Handle(new UpdateProfileCommand(Guid.NewGuid(), "Jane"), CancellationToken.None);

        Assert.True(result.IsFailure);
    }
}
