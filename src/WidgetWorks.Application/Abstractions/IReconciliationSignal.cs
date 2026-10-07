namespace WidgetWorks.Application.Abstractions;

/// <summary>
/// A nudge from the code that creates work to the background worker that clears it.
///
/// Checkout knows the instant an order needs reconciling; the worker would otherwise have to discover
/// it by polling. Polling for something that is almost never there is the expensive way to learn
/// nothing — on a serverless database it also keeps the compute awake — so the producer says so
/// instead.
///
/// Signals are best-effort and in-memory by design: they make the common case fast, and the periodic
/// sweep remains the guarantee. Anything parked before a restart is picked up there rather than lost.
/// </summary>
public interface IReconciliationSignal
{
    /// <summary>Reports that an order now needs reconciling. Never blocks, never throws.</summary>
    void Notify();

    /// <summary>
    /// Waits for a signal, returning early if one arrives and otherwise after
    /// <paramref name="timeout"/>. Coalescing: several signals while nothing is waiting release one
    /// wait, because the worker reads a queue rather than a single item.
    /// </summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken ct);
}
