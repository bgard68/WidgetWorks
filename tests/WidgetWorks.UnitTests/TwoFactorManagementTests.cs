using Microsoft.Extensions.Time.Testing;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.TwoFactor.Confirm;
using WidgetWorks.Application.TwoFactor.Disable;
using WidgetWorks.Application.TwoFactor.Recovery;
using WidgetWorks.Domain.Users;
using WidgetWorks.UnitTests.Fakes;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// Confirming enrollment, disabling 2FA, and signing in with a recovery code. Two invariants are
/// under test throughout: any change to a factor rotates the security stamp (killing other
/// sessions), and a recovery code works exactly once.
/// </summary>
public class TwoFactorManagementTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 9, 30, 0, TimeSpan.Zero);

    private sealed record Ctx(
        FakeTimeProvider Clock,
        InMemoryUserRepository Users,
        InMemoryTwoFactorRepository TwoFactor,
        InMemoryRefreshTokenRepository Refresh,
        FakeTotpService Totp,
        FakeRecoveryCodes Recovery,
        StubTokenService Tokens,
        RecordingAuditLog Audit,
        User User);

    private static Ctx Setup(bool twoFactorEnabled = false)
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
            TwoFactorEnabled = twoFactorEnabled,
        };
        users.Store[user.Id] = user;
        return new Ctx(new FakeTimeProvider(Now), users, new InMemoryTwoFactorRepository(),
            new InMemoryRefreshTokenRepository(), new FakeTotpService(), new FakeRecoveryCodes(),
            new StubTokenService(), new RecordingAuditLog(), user);
    }

    private static ConfirmEnrollHandler Confirm(Ctx c)
        => new(c.Users, c.TwoFactor, c.Totp, c.Recovery, c.Audit, c.Clock);

    private static DisableTwoFactorHandler Disable(Ctx c)
        => new(c.Users, c.TwoFactor, c.Audit);

    private static RecoveryLoginHandler RecoveryLogin(Ctx c)
        => new(c.Users, c.Refresh, c.TwoFactor, c.Recovery, c.Tokens, c.Audit, c.Clock);

    // ---- confirm enrollment --------------------------------------------------------------

    [Fact]
    public async Task Confirm_fails_for_an_unknown_user()
    {
        var c = Setup();
        var result = await Confirm(c).Handle(new ConfirmEnrollCommand(Guid.NewGuid(), "654321"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("User not found.", result.Error);
    }

    [Fact]
    public async Task Confirm_fails_when_enrollment_was_never_started()
    {
        var c = Setup();
        var result = await Confirm(c).Handle(new ConfirmEnrollCommand(c.User.Id, "654321"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("No pending 2FA enrollment. Start enrollment first.", result.Error);
    }

    [Fact]
    public async Task Confirm_rejects_a_wrong_code_and_leaves_2fa_off()
    {
        var c = Setup();
        await c.TwoFactor.UpsertPendingSecretAsync(c.User.Id, "SECRETBASE32", CancellationToken.None);

        var result = await Confirm(c).Handle(new ConfirmEnrollCommand(c.User.Id, "000000"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid authenticator code.", result.Error);
        Assert.False(c.User.TwoFactorEnabled);
        Assert.False(c.TwoFactor.Secrets[c.User.Id].IsConfirmed);
    }

    [Fact]
    public async Task Confirm_enables_2fa_issues_recovery_codes_and_rotates_the_stamp()
    {
        var c = Setup();
        var stampBefore = c.User.SecurityStamp;
        await c.TwoFactor.UpsertPendingSecretAsync(c.User.Id, "SECRETBASE32", CancellationToken.None);

        var result = await Confirm(c).Handle(new ConfirmEnrollCommand(c.User.Id, c.Totp.ValidCode), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Value!.RecoveryCodes.Count);
        Assert.Equal(10, result.Value.RecoveryCodes.Distinct().Count());
        Assert.True(c.User.TwoFactorEnabled);
        Assert.True(c.TwoFactor.Secrets[c.User.Id].IsConfirmed);

        // Turning on a factor must sign other devices out.
        Assert.NotEqual(stampBefore, c.User.SecurityStamp);
        Assert.Contains("2fa.enabled", c.Audit.Actions);
    }

    [Fact]
    public async Task Confirm_replaces_any_previous_recovery_codes()
    {
        var c = Setup();
        await c.TwoFactor.UpsertPendingSecretAsync(c.User.Id, "SECRETBASE32", CancellationToken.None);
        await c.TwoFactor.AddRecoveryCodesAsync(c.User.Id, [c.Recovery.Hash("stale-code")], Now, CancellationToken.None);

        await Confirm(c).Handle(new ConfirmEnrollCommand(c.User.Id, c.Totp.ValidCode), CancellationToken.None);

        // The old set is gone, not merged with the new one.
        var stillUsable = await c.TwoFactor.ConsumeRecoveryCodeAsync(
            c.User.Id, c.Recovery.Hash("stale-code"), Now, CancellationToken.None);
        Assert.False(stillUsable);
    }

    // ---- disable -------------------------------------------------------------------------

    [Fact]
    public async Task Disable_fails_for_an_unknown_user()
    {
        var c = Setup();
        var result = await Disable(c).Handle(new DisableTwoFactorCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("User not found.", result.Error);
    }

    [Fact]
    public async Task Disable_clears_the_secret_and_the_recovery_codes()
    {
        var c = Setup(twoFactorEnabled: true);
        var stampBefore = c.User.SecurityStamp;
        await c.TwoFactor.UpsertPendingSecretAsync(c.User.Id, "SECRETBASE32", CancellationToken.None);
        await c.TwoFactor.AddRecoveryCodesAsync(c.User.Id, [c.Recovery.Hash("code-1")], Now, CancellationToken.None);

        var result = await Disable(c).Handle(new DisableTwoFactorCommand(c.User.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(c.User.TwoFactorEnabled);
        Assert.Empty(c.TwoFactor.Secrets);
        Assert.NotEqual(stampBefore, c.User.SecurityStamp);
        Assert.Contains("2fa.disabled", c.Audit.Actions);

        var stillUsable = await c.TwoFactor.ConsumeRecoveryCodeAsync(
            c.User.Id, c.Recovery.Hash("code-1"), Now, CancellationToken.None);
        Assert.False(stillUsable);
    }

    // ---- recovery-code login -------------------------------------------------------------

    [Fact]
    public async Task Recovery_login_rejects_an_unparseable_challenge()
    {
        var c = Setup(twoFactorEnabled: true);
        var result = await RecoveryLogin(c).Handle(new RecoveryLoginCommand("not-a-challenge", "code-1"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid or expired challenge.", result.Error);
    }

    [Fact]
    public async Task Recovery_login_rejects_a_challenge_for_a_user_who_is_gone()
    {
        var c = Setup(twoFactorEnabled: true);
        var challenge = c.Tokens.CreateChallengeToken(c.User);
        c.Users.Store.Clear();

        var result = await RecoveryLogin(c).Handle(new RecoveryLoginCommand(challenge, "code-1"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid challenge.", result.Error);
    }

    [Fact]
    public async Task Recovery_login_rejects_a_user_who_never_enabled_2fa()
    {
        var c = Setup(twoFactorEnabled: false);
        var challenge = c.Tokens.CreateChallengeToken(c.User);

        var result = await RecoveryLogin(c).Handle(new RecoveryLoginCommand(challenge, "code-1"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid challenge.", result.Error);
    }

    [Fact]
    public async Task Recovery_login_with_an_unknown_code_is_audited_and_refused()
    {
        var c = Setup(twoFactorEnabled: true);
        var challenge = c.Tokens.CreateChallengeToken(c.User);

        var result = await RecoveryLogin(c).Handle(new RecoveryLoginCommand(challenge, "never-issued"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Invalid recovery code.", result.Error);
        Assert.Contains("2fa.recovery_failed", c.Audit.Actions);
        Assert.Empty(c.Refresh.Tokens);
    }

    [Fact]
    public async Task Recovery_login_signs_in_and_issues_a_refresh_token()
    {
        var c = Setup(twoFactorEnabled: true);
        await c.TwoFactor.AddRecoveryCodesAsync(c.User.Id, [c.Recovery.Hash("code-1")], Now, CancellationToken.None);
        var challenge = c.Tokens.CreateChallengeToken(c.User);

        var result = await RecoveryLogin(c).Handle(new RecoveryLoginCommand(challenge, "code-1"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("access-token", result.Value!.AccessToken);
        Assert.Equal(UserRoles.Customer, result.Value.Role);
        var issued = Assert.Single(c.Refresh.Tokens);
        Assert.Equal(c.User.Id, issued.UserId);
        Assert.Equal(Now, issued.CreatedAt);
        Assert.Contains("2fa.recovery_success", c.Audit.Actions);
    }

    [Theory]
    [InlineData("  CODE-1  ")]
    [InlineData("Code-1")]
    public async Task Recovery_codes_are_matched_case_and_whitespace_insensitively(string entered)
    {
        var c = Setup(twoFactorEnabled: true);
        await c.TwoFactor.AddRecoveryCodesAsync(c.User.Id, [c.Recovery.Hash("code-1")], Now, CancellationToken.None);
        var challenge = c.Tokens.CreateChallengeToken(c.User);

        var result = await RecoveryLogin(c).Handle(new RecoveryLoginCommand(challenge, entered), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task A_recovery_code_works_exactly_once()
    {
        var c = Setup(twoFactorEnabled: true);
        await c.TwoFactor.AddRecoveryCodesAsync(c.User.Id, [c.Recovery.Hash("code-1")], Now, CancellationToken.None);
        var challenge = c.Tokens.CreateChallengeToken(c.User);

        var first = await RecoveryLogin(c).Handle(new RecoveryLoginCommand(challenge, "code-1"), CancellationToken.None);
        var second = await RecoveryLogin(c).Handle(new RecoveryLoginCommand(challenge, "code-1"), CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal("Invalid recovery code.", second.Error);
        Assert.Single(c.Refresh.Tokens);
    }

    /// <summary>Deterministic stand-in for the real generator: the "hash" is just a marked prefix.</summary>
    private sealed class FakeRecoveryCodes : IRecoveryCodes
    {
        public IReadOnlyList<RecoveryCode> Generate(int count)
            => Enumerable.Range(1, count).Select(i => new RecoveryCode($"code-{i}", Hash($"code-{i}"))).ToList();

        public string Hash(string code) => "rc:" + code;
    }
}
