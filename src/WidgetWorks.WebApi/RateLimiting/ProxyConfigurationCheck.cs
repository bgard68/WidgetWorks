namespace WidgetWorks.WebApi.RateLimiting;

/// <summary>
/// Notices the one configuration mistake that turns throttling into an outage, and says so.
///
/// Rate limiting partitions by caller. Behind a reverse proxy every request arrives carrying the
/// proxy's address, so unless <c>X-Forwarded-For</c> is trusted, every caller in the world collapses
/// into a single partition and the limiter becomes a global cap that the first busy minute trips for
/// everybody. Nothing about that is visible in a log: requests simply start returning 429.
///
/// The inverse mistake is the security one — trusting the header with no proxy in front lets a
/// caller forge it and mint a fresh partition per request, opting out of throttling entirely.
///
/// Both are silent, so this watches real traffic and warns once for whichever it sees.
/// </summary>
public sealed class ProxyConfigurationCheck(RateLimitOptions options, ILogger<ProxyConfigurationCheck> logger)
{
    private int _warned;

    /// <summary>Inspects one request. Cheap after the first warning, and warns at most once.</summary>
    public void Inspect(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Volatile.Read(ref _warned) != 0)
        {
            return;
        }

        var forwarded = context.Request.Headers.ContainsKey("X-Forwarded-For");

        if (forwarded && !options.TrustForwardedFor)
        {
            WarnOnce(
                "Requests carry X-Forwarded-For but RateLimiting:TrustForwardedFor is false, so every " +
                "caller shares one throttling partition and the limits apply to all traffic combined. " +
                "Set it true if a trusted proxy sits in front of this app.");
        }
        else if (!forwarded && options.TrustForwardedFor)
        {
            WarnOnce(
                "RateLimiting:TrustForwardedFor is true but requests arrive without X-Forwarded-For. " +
                "If no proxy is in front, a caller can forge that header and give itself an " +
                "unlimited number of throttling partitions. Set it false unless a proxy is guaranteed.");
        }
    }

    private void WarnOnce(string message)
    {
        // Interlocked so a burst of concurrent requests produces one warning rather than a page of
        // identical ones, which is how a real signal gets scrolled past.
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning("{Message}", message);
        }
    }
}
