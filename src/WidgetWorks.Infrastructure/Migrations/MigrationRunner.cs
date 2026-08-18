using System.Reflection;
using DbUp;

namespace WidgetWorks.Infrastructure.Migrations;

/// <summary>Result of a migration attempt — success, or why it failed and after how many tries.</summary>
public sealed record MigrationOutcome(bool Successful, string? Error, int Attempts)
{
    public static MigrationOutcome Ok(int attempts) => new(true, null, attempts);
}

public static class MigrationRunner
{
    /// <summary>
    /// Creates the database if needed and applies all embedded SQL migrations in order.
    /// Throws on failure — use this where failing fast is wanted (tests, local development).
    /// </summary>
    public static void Run(string connectionString)
    {
        var outcome = TryRun(connectionString, maxAttempts: 1);
        if (!outcome.Successful)
        {
            throw new InvalidOperationException($"Database migration failed. {outcome.Error}");
        }
    }

    /// <summary>
    /// Applies migrations, retrying with exponential backoff, and reports the outcome instead of
    /// throwing.
    ///
    /// Two problems this solves. A serverless database (Neon, or Azure SQL serverless) suspends
    /// when idle and takes seconds to wake, so the first attempt after a quiet period can fail
    /// against a perfectly healthy database — retrying absorbs that. And a genuinely bad
    /// connection string used to throw out of startup, which exits the process; a host then
    /// restarts it, it fails again, and the loop quietly consumes an entire day of a free tier's
    /// CPU allowance. Returning the failure lets the caller start anyway and report unhealthy,
    /// which costs nothing and is far easier to diagnose than a restart loop.
    /// </summary>
    public static MigrationOutcome TryRun(
        string connectionString,
        int maxAttempts = 4,
        TimeSpan? firstDelay = null,
        Action<string>? log = null)
    {
        var delay = firstDelay ?? TimeSpan.FromSeconds(2);
        var write = log ?? Console.WriteLine;
        string? lastError = null;

        for (var attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
        {
            try
            {
                EnsureDatabase.For.PostgresqlDatabase(connectionString);

                var upgrader = DeployChanges.To
                    .PostgresqlDatabase(connectionString)
                    .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
                    .LogToConsole()
                    .Build();

                var result = upgrader.PerformUpgrade();
                if (result.Successful)
                {
                    return MigrationOutcome.Ok(attempt);
                }

                lastError = result.Error?.Message ?? "Unknown migration error.";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            if (attempt < maxAttempts)
            {
                write($"[migrations] attempt {attempt}/{maxAttempts} failed: {lastError}. " +
                      $"Retrying in {delay.TotalSeconds:0}s (the database may be waking).");
                Thread.Sleep(delay);
                delay = TimeSpan.FromTicks(delay.Ticks * 2);
            }
        }

        return new MigrationOutcome(false, lastError, Math.Max(1, maxAttempts));
    }
}
