using WidgetWorks.Application.Abstractions;
using WidgetWorks.Application.Checkout.Reconcile;

namespace WidgetWorks.WebApi.Hosting;

/// <summary>
/// Chases unconfirmed charges promptly, rather than whenever the hourly reservation sweep next comes
/// round.
///
/// Riding that sweep was correct but slow: an order whose charge outcome is unknown would sit holding
/// stock for up to an hour before anyone asked the provider what happened. For the one case where the
/// customer may already have been charged, an hour is the wrong answer.
///
/// A naive fix — a short timer — would be worse than the problem. Polling every minute pins the
/// serverless database awake around the clock, which is precisely the cost the hourly interval exists
/// to avoid, and it would do so to look at a table that is empty on every healthy day.
///
/// So this wakes on demand instead. Checkout signals it the moment it parks an unconfirmed order, and
/// the loop then stays on a short cycle only while work remains, falling back to a long idle wait once
/// the queue is clear. On a good day it sleeps and touches nothing; during an incident it is a minute
/// behind. The hourly sweep keeps its own reconciliation pass as the backstop for anything parked
/// before a restart, since this signal lives in memory.
/// </summary>
public sealed class PaymentReconciliationSweeper(
    IServiceScopeFactory scopes,
    ReconciliationOptions options,
    IReconciliationSignal signal,
    ILogger<PaymentReconciliationSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Payment reconciliation is disabled by configuration.");
            return;
        }

        var busy = TimeSpan.FromSeconds(Math.Max(5, options.BusyIntervalSeconds));
        var idle = TimeSpan.FromMinutes(Math.Max(1, options.IdleIntervalMinutes));

        logger.LogInformation(
            "Payment reconciliation waiting on checkout, or every {Idle} when quiet, then every {Busy} while work remains.",
            idle,
            busy);

        // Starts by waiting rather than sweeping: a freshly started host has nothing to reconcile that
        // the reservation sweep's own pass will not pick up, and startup is the worst moment to add
        // database work.
        var wait = idle;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Returns early the instant checkout parks an order, so the common case is not waiting out
            // a timer at all.
            await signal.WaitAsync(wait, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var scope = scopes.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<ReconcileUnconfirmedPaymentsHandler>();
                var summary = await handler.Handle(stoppingToken);

                // Anything still unresolved keeps the loop on the short cycle; a clear queue lets the
                // database go back to sleep.
                wait = summary.StillUnknown > 0 ? busy : idle;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad pass must not end the loop — that would leave orders holding stock with
                // nothing coming back for them, and nothing saying so. Backed off to the idle interval
                // so a persistent fault cannot turn into a tight retry loop against the provider.
                logger.LogError(ex, "Payment reconciliation pass failed; backing off until the next wake.");
                wait = idle;
            }
        }
    }
}
