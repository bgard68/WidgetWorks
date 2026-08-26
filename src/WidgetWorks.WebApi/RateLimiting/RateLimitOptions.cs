namespace WidgetWorks.WebApi.RateLimiting;

/// <summary>
/// Throttling budgets, bound from the <c>RateLimiting</c> configuration section so an operator can
/// tighten a limit during an incident without a redeploy. Defaults are deliberately generous enough
/// that a real customer never meets them and tight enough that scripted abuse does.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>
    /// Whether an <c>X-Forwarded-For</c> header may be believed when identifying the caller.
    ///
    /// This is the setting that decides whether the limiter works at all behind a reverse proxy.
    /// Left false while hosted behind one, every request appears to originate from the proxy, all
    /// callers collapse into a single partition, and the limiter turns into a global cap that the
    /// first busy minute trips for everybody — a self-inflicted outage. Set true only when a proxy
    /// you control is guaranteed to be in front, because a client can otherwise forge the header
    /// and mint itself unlimited partitions.
    ///
    /// Setting it true is necessary but not sufficient: which entry of the chain is believed matters
    /// just as much, and that is <see cref="TrustedProxyHops"/>.
    /// </summary>
    public bool TrustForwardedFor { get; set; }

    /// <summary>
    /// How many proxies we control sit in front of this app, each appending one entry to
    /// <c>X-Forwarded-For</c>. One for Azure App Service; two if a CDN or WAF is put in front of it.
    ///
    /// This decides *which* entry is believed, and it matters because a proxy appends rather than
    /// overwrites: whatever the caller sent stays in the header, to the left of it. Counting this
    /// many entries back from the right lands on one a trusted hop wrote.
    ///
    /// Too high and the count runs off the front of the chain, every caller falls back to the proxy's
    /// own address, and the limiter becomes the global cap described above. Too low and it reads an
    /// entry nearer the caller's end — far enough, one the caller wrote themselves.
    /// <see cref="ProxyConfigurationCheck"/> warns when live traffic disagrees with this number.
    /// </summary>
    public int TrustedProxyHops { get; set; } = 1;

    /// <summary>Sign-in, registration and password-reset requests.</summary>
    public RateLimitBudget Auth { get; set; } = new() { PermitLimit = 20, WindowSeconds = 60 };

    /// <summary>Order placement. Guards card testing and the inventory-reservation abuse path.</summary>
    public RateLimitBudget Checkout { get; set; } = new() { PermitLimit = 8, WindowSeconds = 60 };

    /// <summary>Guest order lookup, which confirms an order number against an email.</summary>
    public RateLimitBudget Lookup { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };
}

/// <summary>A fixed-window budget: <see cref="PermitLimit"/> requests per <see cref="WindowSeconds"/>.</summary>
public sealed class RateLimitBudget
{
    public int PermitLimit { get; set; } = 10;

    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Coerces the configured pair into a usable window. A zero or negative value from configuration
    /// would otherwise throw deep inside the limiter at first request rather than at startup, so it
    /// falls back to the property default instead of taking the process down.
    /// </summary>
    public (int Permits, TimeSpan Window) Resolve()
        => (PermitLimit > 0 ? PermitLimit : 10, TimeSpan.FromSeconds(WindowSeconds > 0 ? WindowSeconds : 60));
}
