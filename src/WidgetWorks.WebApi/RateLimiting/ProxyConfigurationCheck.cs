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
/// The third is trusting the header correctly but counting the wrong entry of it, which lands back
/// on the global cap without the setting looking wrong.
///
/// All three are silent, so this watches real traffic and warns once for whichever it sees.
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
        else if (forwarded && options.TrustForwardedFor && ChainLength(context) < options.TrustedProxyHops)
        {
            // The hop count decides which entry of the chain is believed. Too high and it runs off
            // the front on every request, each one silently falling back to the connection address —
            // which is the proxy, so this reproduces the global-cap outage above while the setting
            // that would explain it reads as correct.
            WarnOnce(
                "RateLimiting:TrustedProxyHops is higher than the number of X-Forwarded-For entries " +
                "arriving, so the caller cannot be identified and every request falls back to the " +
                "proxy address — throttling all traffic as one caller. Set it to the number of " +
                "proxies in front of this app (one for Azure App Service).");
        }
    }

    /// <summary>Entries in the chain, counted the way <see cref="ClientAddress"/> reads it.</summary>
    private static int ChainLength(HttpContext context)
    {
        var length = 0;
        foreach (var value in context.Request.Headers["X-Forwarded-For"])
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                length += value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            }
        }

        return length;
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
