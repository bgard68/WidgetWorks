using WidgetWorks.Application.Checkout.PlaceOrder;
using WidgetWorks.Application.Checkout.Reconcile;
using WidgetWorks.Application.Checkout.ReleaseStale;

namespace WidgetWorks.WebApi.Hosting;

/// <summary>
/// Runs <see cref="ReleaseStaleReservationsHandler"/> on a timer.
///
/// This type is only the clock. All of the policy — how stale is stale, how many to take, what
/// releasing means — belongs to the handler, which is why that part can be tested without waiting
/// for a timer to tick.
/// </summary>
public sealed class ReservationSweeper(
    IServiceScopeFactory scopes,
    ReservationOptions options,
    ReconciliationOptions reconciliation,
    ILogger<ReservationSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Reservation sweep is disabled by configuration.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.SweepIntervalMinutes));
        logger.LogInformation(
            "Reservation sweep running every {Interval}, releasing orders unsettled for {ExpireAfter} minutes.",
            interval,
            options.ExpireAfterMinutes);

        using var timer = new PeriodicTimer(interval);

        // Waits a full interval before the first pass on purpose. Startup is the worst moment to add
        // database work, and nothing is so urgent that it cannot wait one interval.
        while (await SafeWaitAsync(timer, stoppingToken))
        {
            // A scope per tick, because the repositories are registered Scoped. A long-lived
            // singleton holding a scoped dependency is the captive-dependency bug: it would pin one
            // connection for the lifetime of the process.
            using var scope = scopes.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ReleaseStaleReservationsHandler>();
            var purge = scope.ServiceProvider.GetRequiredService<PurgeIdempotencyKeysHandler>();
            var reconcile = scope.ServiceProvider.GetRequiredService<ReconcileUnconfirmedPaymentsHandler>();

            try
            {
                // Reconciliation runs FIRST, and the order matters. An order whose charge was never
                // confirmed is exempt from the stale sweep precisely so it cannot be failed on a
                // timer; resolving it here first means that by the time the sweep looks, any order it
                // can see has a known payment outcome.
                if (reconciliation.Enabled)
                {
                    await reconcile.Handle(stoppingToken);
                }

                await handler.Handle(stoppingToken);

                // Rides along on this tick rather than bringing its own timer. Both are the same
                // kind of work — forgetting something whose time has passed — and a second
                // PeriodicTimer would wake the serverless database on its own schedule, which is
                // precisely the cost this interval was chosen to avoid.
                await purge.Handle(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown, not a fault. Leave the loop quietly.
                break;
            }
            catch (Exception ex)
            {
                // One bad sweep must not end the loop: a transient database blip would otherwise
                // silently stop reclaiming stock for the lifetime of the process, and nothing would
                // report it. Logged, then the next tick tries again.
                logger.LogError(ex, "Reservation sweep failed; the next pass will retry.");
            }
        }
    }

    /// <summary>
    /// Waits for the next tick, reporting false once the host is stopping. Wrapped because
    /// <see cref="PeriodicTimer.WaitForNextTickAsync"/> throws on cancellation, and a cancelled wait
    /// during shutdown is an ordinary ending rather than an error worth surfacing.
    /// </summary>
    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
