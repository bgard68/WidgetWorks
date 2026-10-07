using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Application.Checkout.PlaceOrder;

/// <summary>
/// Drops idempotency keys older than the retention window.
///
/// A ledger that only ever grows is a slow leak: every checkout adds a row, nothing removes it, and
/// the table outgrows its usefulness long before anyone notices. Retention is also what makes the
/// stuck-claim case survivable — a request that died between claiming a key and recording its
/// outcome holds that key until this sweep clears it.
///
/// Scheduling lives elsewhere, as with the reservation sweep, so the policy can be tested without a
/// timer.
/// </summary>
public sealed class PurgeIdempotencyKeysHandler(
    IIdempotencyStore store,
    IdempotencyOptions options,
    TimeProvider clock,
    ILogger<PurgeIdempotencyKeysHandler> logger)
{
    /// <summary>Runs one pass. Returns how many keys were forgotten.</summary>
    public async Task<int> Handle(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().AddHours(-Math.Max(1, options.RetentionHours));
        var purged = await store.PurgeExpiredAsync(cutoff, ct);

        if (purged > 0)
        {
            logger.LogInformation("Forgot {Purged} idempotency key(s) claimed before {Cutoff:o}.", purged, cutoff);
        }

        return purged;
    }
}
