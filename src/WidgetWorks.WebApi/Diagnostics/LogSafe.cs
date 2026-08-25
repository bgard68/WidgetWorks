namespace WidgetWorks.WebApi.Diagnostics;

/// <summary>
/// Makes caller-controlled text safe to put in a log message.
///
/// A log file is a flat stream of lines, so any value carrying a newline can end one entry and
/// begin another that reads exactly like a genuine record — an attacker choosing what the log
/// appears to say about them (CWE-117). Request paths are the easy example: <c>Path.Value</c> is
/// the *decoded* path, so <c>%0A</c> in a URL arrives as a real line break.
/// </summary>
public static class LogSafe
{
    /// <summary>Shown in place of a value that sanitised away to nothing, so the field is never blank.</summary>
    public const string Empty = "(empty)";

    /// <summary>
    /// Strips control characters and truncates. Everything printable is kept, because the point is
    /// to preserve what was actually requested — a path full of odd characters is exactly what an
    /// operator needs to see — while removing the ones that can restructure the log itself.
    /// </summary>
    public static string Text(string? value, int maxLength = 256)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Empty;
        }

        var kept = new char[Math.Min(value.Length, maxLength)];
        var length = 0;

        foreach (var c in value)
        {
            if (length == kept.Length)
            {
                break;
            }

            // char.IsControl covers CR, LF, tab and the rest of the C0/C1 ranges — the characters
            // that can break a line or confuse a log viewer.
            if (!char.IsControl(c))
            {
                kept[length++] = c;
            }
        }

        return length == 0 ? Empty : new string(kept, 0, length);
    }
}
