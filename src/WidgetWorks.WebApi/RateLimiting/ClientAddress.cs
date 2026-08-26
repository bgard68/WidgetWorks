using System.Net;
using Microsoft.AspNetCore.Http;

namespace WidgetWorks.WebApi.RateLimiting;

/// <summary>
/// Works out which caller a request belongs to, so throttling partitions by client rather than by
/// process. Kept separate from the limiter wiring because this is the part with rules worth testing:
/// everything else is framework configuration.
/// </summary>
public static class ClientAddress
{
    /// <summary>Partition used when no address can be determined, so those callers share a budget.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Resolves the partition key for <paramref name="context"/>.
    ///
    /// <c>X-Forwarded-For</c> is read only when <paramref name="trustForwardedFor"/> says a proxy we
    /// control is in front — and even then, only the entry that proxy wrote itself.
    ///
    /// The leftmost entry is *not* the client. A proxy appends the peer it received from; it does not
    /// overwrite what arrived. A caller sending <c>X-Forwarded-For: 9.9.9.9</c> reaches this app as
    /// <c>9.9.9.9, &lt;real client&gt;</c>, so reading position zero reads a value the attacker chose.
    /// Varying it per request mints a fresh partition every time and opts out of throttling entirely —
    /// the forgery <see cref="RateLimitOptions.TrustForwardedFor"/> warns about, reached through the
    /// other door, and not closed by having that flag correctly set.
    ///
    /// Counting from the right instead lands on an entry a trusted hop wrote. Everything to its left
    /// is caller-supplied and ignored.
    /// </summary>
    /// <param name="trustedProxyHops">
    /// How many proxies we control sit in front, each appending one entry. One for Azure App Service.
    /// </param>
    public static string Resolve(HttpContext context, bool trustForwardedFor, int trustedProxyHops = 1)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (trustForwardedFor)
        {
            var forwarded = ClientFromChain(context.Request.Headers["X-Forwarded-For"], trustedProxyHops);
            if (forwarded is not null)
            {
                return forwarded;
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? Unknown;
    }

    /// <summary>
    /// Picks the entry written by the outermost hop we trust, counting from the right.
    ///
    /// With <c>h</c> trusted proxies the client sits at <c>count - h</c>: the innermost proxy appended
    /// the hop before it, and so on outward, so each trusted hop accounts for one entry from the end.
    /// A chain shorter than <c>h</c> means the header did not come from the proxy chain we expect, so
    /// this returns null and the caller falls back to the connection address rather than trusting it.
    /// </summary>
    private static string? ClientFromChain(IEnumerable<string?> headerValues, int trustedProxyHops)
    {
        // A zero or negative hop count from configuration would index past the end of every chain and
        // silently collapse every caller into the connection address, so it falls back to one proxy.
        var hops = trustedProxyHops > 0 ? trustedProxyHops : 1;

        var chain = new List<string>();
        foreach (var value in headerValues)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // A chain may arrive as one comma-separated header or as several repeated headers, and
            // the two are equivalent — a recipient is free to combine them, in order.
            chain.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var index = chain.Count - hops;
        return index >= 0 && index < chain.Count ? Normalize(chain[index]) : null;
    }

    /// <summary>
    /// Reduces one chain entry to a bare address.
    ///
    /// App Service appends the client as <c>ip:port</c>, and the source port is ephemeral — a new one
    /// per connection. Left on, this would partition per request rather than per caller, which is the
    /// same escape reading from the right exists to close.
    ///
    /// Anything that is not an address is discarded rather than used as a key: the <c>unknown</c> and
    /// obfuscated forms the standard permits are not identities, and treating one as a key would give
    /// every caller sending that placeholder a shared budget under a name that reads like a specific
    /// client.
    /// </summary>
    private static string? Normalize(string candidate)
    {
        // Tried first so a bare IPv6 address is not mistaken for a host:port pair on account of its
        // colons.
        if (IPAddress.TryParse(candidate, out var address))
        {
            return address.ToString();
        }

        // Covers "203.0.113.7:51514" and the bracketed "[2001:db8::1]:51514" that App Service writes.
        return IPEndPoint.TryParse(candidate, out var endpoint) ? endpoint.Address.ToString() : null;
    }
}
