using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Application.Checkout.ReleaseStale;

/// <summary>
/// How long an unsettled order may hold stock, and how often to look. Bound from the
/// <c>Reservations</c> configuration section.
/// </summary>
public sealed class ReservationOptions
{
    /// <summary>
    /// How long an order may sit in AwaitingPayment before its stock is handed back.
    ///
    /// This is a trade, not a tuning knob: too short and a slow but honest bank redirect loses a
    /// customer's basket; too long and abandoned or abusive orders hold the catalogue hostage.
    /// Fifteen minutes is longer than any interactive redirect and short enough that a shopper who
    /// returns to an out-of-stock item is rare.
    /// </summary>
    public int ExpireAfterMinutes { get; set; } = 15;

    /// <summary>How often the sweep runs.</summary>
    public int SweepIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Most orders released in one pass. A backlog is worked through over several sweeps rather
    /// than in one long pass, so a bad day cannot turn into a slow transaction storm.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Turns the sweep off — for a host that should not run background work.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Hands back stock held by orders whose payment never settled.
///
/// Checkout reserves stock the moment an order is placed. When settlement is asynchronous the order
/// parks in AwaitingPayment and waits for a provider webhook. If that webhook never arrives — a
/// provider outage, a dropped delivery, a shopper who closed the tab at the bank's redirect — the
/// reservation is held forever, and without this sweep the only route back is an administrator
/// editing inventory counts by hand.
///
/// The release itself reuses <see cref="IOrderRepository.MarkPaymentFailedAsync"/>: it already sets
/// the status and releases the reservation in one transaction, and its compare-and-set means a
/// webhook landing at the same moment as a sweep cannot both act. PaymentFailed is also the honest
/// description of a settlement that never came, so no new status is needed.
///
/// Scheduling deliberately lives elsewhere. This type is a plain handler so the policy can be tested
/// without a timer, a host, or a clock that really waits.
/// </summary>
public sealed class ReleaseStaleReservationsHandler(
    IOrderRepository orders,
    TimeProvider clock,
    ReservationOptions options,
    ILogger<ReleaseStaleReservationsHandler> logger)
{
    /// <summary>Runs one sweep. Returns how many orders had their stock returned to sale.</summary>
    public async Task<int> Handle(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cutoff = now.AddMinutes(-Math.Max(1, options.ExpireAfterMinutes));
        var stale = await orders.GetStaleAwaitingPaymentAsync(cutoff, Math.Max(1, options.BatchSize), ct);

        var released = 0;
        foreach (var order in stale)
        {
            // Checked between orders rather than only at the top: a shutdown midway through a large
            // batch should stop cleanly, and each release is already committed on its own.
            ct.ThrowIfCancellationRequested();

            if (await orders.MarkPaymentFailedAsync(order, "Payment was not completed in time.", now, ct))
            {
                released++;
            }
            else
            {
                // The order moved on between the query and the write — almost always a webhook
                // that landed first, which is the good outcome. Recorded at debug because it is
                // expected, not a fault.
                logger.LogDebug(
                    "Order {OrderNumber} settled before the sweep reached it; nothing released.",
                    order.OrderNumber);
            }
        }

        if (released > 0)
        {
            // Worth a real log line: it means customers or scripts are abandoning payments, and a
            // rising count is the signal that something upstream is wrong.
            logger.LogInformation(
                "Released stock held by {Released} order(s) unsettled since before {Cutoff:o}.",
                released,
                cutoff);
        }

        return released;
    }
}
