using Microsoft.Extensions.Logging;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Hosting;

/// <summary>
/// The in-process implementation: a semaphore used as a one-slot doorbell.
///
/// Capped at one permit on purpose. The worker's next pass reads every outstanding order, so ten
/// signals and one signal call for the same single pass; letting permits accumulate would just queue
/// up redundant sweeps. A signal arriving while a pass is already running is not lost either — it
/// leaves the permit set, so the loop goes straight round again rather than sleeping on work it has
/// not yet seen.
/// </summary>
public sealed class ReconciliationSignal(ILogger<ReconciliationSignal> logger) : IReconciliationSignal, IDisposable
{
    private readonly SemaphoreSlim _doorbell = new(0, 1);

    public void Notify()
    {
        try
        {
            _doorbell.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already rung and not yet answered. Expected, and exactly the coalescing described above:
            // the pending pass will see this order too.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown raced the signal. The order stays marked in the database and the periodic sweep
            // collects it on the next boot, so nothing is lost by dropping the nudge.
            logger.LogDebug("Reconciliation signal arrived during shutdown; the periodic sweep will cover it.");
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _doorbell.WaitAsync(timeout, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the caller checks the token.
        }
    }

    public void Dispose() => _doorbell.Dispose();
}
