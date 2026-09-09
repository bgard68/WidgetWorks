using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Checkout.ReleaseStale;
using WidgetWorks.WebApi.Hosting;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// The hosted service that puts the reservation sweep on a timer.
///
/// Only the switch is covered here, and deliberately so: the sweep's policy is proven against the
/// handler in the unit suite, and the loop below the switch is driven by a <c>PeriodicTimer</c> the
/// service constructs itself on a minimum one-minute interval. Reaching it would mean either waiting
/// a real minute or opening a seam in the production type for the benefit of a test, and neither is
/// worth it for a body whose only job is to call a handler that is already covered.
///
/// The switch is worth pinning on its own. It is what a host that should not run background work
/// sets, and a sweeper that ignored it would quietly release stock from a second instance.
/// </summary>
public class ReservationSweeperTests
{
    private sealed class CapturingLogger : ILogger<ReservationSweeper>
    {
        public readonly List<string> Messages = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    /// <summary>A real scope factory rather than a stand-in — the disabled path must not use it.</summary>
    private static IServiceScopeFactory ScopeFactory()
        => new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    [Fact]
    public async Task StartAsync_SweepDisabledByConfiguration_StopsImmediatelyAndSaysWhy()
    {
        // Arrange — the setting a host that should not run background work uses.
        var log = new CapturingLogger();
        var sweeper = new ReservationSweeper(ScopeFactory(), new ReservationOptions { Enabled = false }, log);

        // Act
        await sweeper.StartAsync(CancellationToken.None);

        // Assert — the background task runs to completion rather than parking on a timer. Awaited
        // rather than inspected because the host schedules ExecuteAsync instead of running it
        // inline, so it is still WaitingForActivation the instant StartAsync returns. A sweeper that
        // ignored the switch would sit on its timer and this would time out.
        await sweeper.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TaskStatus.RanToCompletion, sweeper.ExecuteTask.Status);

        // It records why: silently doing nothing looks identical to being broken.
        Assert.Contains("Reservation sweep is disabled by configuration.", log.Messages);

        await sweeper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_SweepEnabled_StaysRunningAndReportsItsSchedule()
    {
        // Arrange — the shipped default.
        var log = new CapturingLogger();
        var sweeper = new ReservationSweeper(
            ScopeFactory(),
            new ReservationOptions { Enabled = true, SweepIntervalMinutes = 5, ExpireAfterMinutes = 15 },
            log);

        // Act
        await sweeper.StartAsync(CancellationToken.None);

        // Assert — it parks on its timer instead of returning, which is what the disabled case has
        // to be different from. Asserting the wait times out proves that positively, where checking
        // IsCompleted immediately would pass even for a sweeper that was about to return.
        await Assert.ThrowsAsync<TimeoutException>(
            () => sweeper.ExecuteTask!.WaitAsync(TimeSpan.FromMilliseconds(250)));

        // The schedule it settled on is logged where an operator can check it against the config.
        // Reached only after ExecuteAsync is past the switch, so the wait above also orders this.
        Assert.Contains(log.Messages, m =>
            m.Contains("00:05:00", StringComparison.Ordinal) && m.Contains("15", StringComparison.Ordinal));

        await sweeper.StopAsync(CancellationToken.None);
    }
}
