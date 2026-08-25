namespace WidgetWorks.WebApi.Diagnostics;

/// <summary>
/// Gives every request an identifier the caller can quote back.
///
/// Without one, a customer saying "checkout failed around three" is the entire incident report:
/// the response carried nothing, and the log line for their exception sits among every other line
/// from that minute with no way to tell them apart. With one, six characters turn diagnosis into
/// a lookup.
/// </summary>
public static class CorrelationId
{
    /// <summary>Header the id is read from and echoed on, matching the de-facto convention.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Longest inbound id accepted, so a caller cannot push arbitrary text into the logs.</summary>
    private const int MaxLength = 64;

    /// <summary>
    /// Resolves the id for a request: an inbound one when the caller supplied something sane, so a
    /// trace already begun upstream keeps its thread, otherwise the id ASP.NET already assigns.
    ///
    /// Inbound values are sanitised rather than trusted. This string ends up in log messages, and
    /// text carrying newlines could otherwise forge whole log entries (CWE-117) — the same class of
    /// defect already fixed once in this codebase, on the order-status path.
    /// </summary>
    public static string Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var supplied = context.Request.Headers[HeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            var cleaned = Sanitize(supplied);
            if (cleaned.Length > 0)
            {
                return cleaned;
            }
        }

        return context.TraceIdentifier;
    }

    /// <summary>Keeps letters, digits and a few separators; drops everything else and truncates.</summary>
    private static string Sanitize(string value)
    {
        var kept = new char[Math.Min(value.Length, MaxLength)];
        var length = 0;

        foreach (var c in value)
        {
            if (length == kept.Length)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.')
            {
                kept[length++] = c;
            }
        }

        return new string(kept, 0, length);
    }
}
