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
    /// <c>X-Forwarded-For</c> is a client-supplied header and is read only when
    /// <paramref name="trustForwardedFor"/> says a trusted proxy is in front. Its leftmost entry is
    /// the original client; entries to the right are the proxies it passed through.
    /// </summary>
    public static string Resolve(HttpContext context, bool trustForwardedFor)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (trustForwardedFor)
        {
            var forwarded = FirstForwardedFor(context.Request.Headers["X-Forwarded-For"]);
            if (forwarded is not null)
            {
                return forwarded;
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? Unknown;
    }

    /// <summary>
    /// Takes the leftmost address from an <c>X-Forwarded-For</c> chain, which may arrive as one
    /// comma-separated header or as several repeated headers. Returns null when nothing usable is
    /// present so the caller can fall back to the connection address.
    /// </summary>
    private static string? FirstForwardedFor(IEnumerable<string?> headerValues)
    {
        foreach (var value in headerValues)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var candidate in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (candidate.Length > 0)
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
