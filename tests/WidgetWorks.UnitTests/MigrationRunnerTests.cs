using WidgetWorks.Infrastructure.Migrations;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// Startup must not turn an unreachable database into a restart loop: throwing out of startup
/// exits the process, the host restarts it, and it fails again — silently consuming a free tier's
/// daily CPU allowance. TryRun reports the failure so the app can start and answer /health.
/// </summary>
public class MigrationRunnerTests
{
    // Port 1 on loopback refuses immediately, so these tests are fast and need no database.
    private const string Unreachable =
        "Host=127.0.0.1;Port=1;Database=nope;Username=u;Password=p;Timeout=1;Command Timeout=1";

    [Fact]
    public void TryRun_reports_failure_instead_of_throwing()
    {
        var outcome = MigrationRunner.TryRun(Unreachable, maxAttempts: 1, firstDelay: TimeSpan.Zero);

        Assert.False(outcome.Successful);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
        Assert.Equal(1, outcome.Attempts);
    }

    [Fact]
    public void TryRun_retries_before_giving_up()
    {
        var logged = new List<string>();

        var outcome = MigrationRunner.TryRun(
            Unreachable, maxAttempts: 3, firstDelay: TimeSpan.FromMilliseconds(1), log: logged.Add);

        Assert.False(outcome.Successful);
        Assert.Equal(3, outcome.Attempts);

        // A message between attempts, but none after the final one.
        Assert.Equal(2, logged.Count);
        Assert.All(logged, line => Assert.Contains("Retrying", line));
    }

    [Fact]
    public void Run_still_throws_so_local_development_fails_fast()
    {
        // Tests and `docker compose up` want the opposite of production: stop immediately and say so.
        Assert.Throws<InvalidOperationException>(() => MigrationRunner.Run(Unreachable));
    }

    [Fact]
    public void Ok_outcome_carries_no_error()
    {
        var outcome = MigrationOutcome.Ok(attempts: 2);

        Assert.True(outcome.Successful);
        Assert.Null(outcome.Error);
        Assert.Equal(2, outcome.Attempts);
    }
}
