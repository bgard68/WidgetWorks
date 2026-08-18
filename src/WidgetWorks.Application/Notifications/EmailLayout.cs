namespace WidgetWorks.Application.Notifications;

/// <summary>
/// Wraps template fragments in a minimal, mail-client-safe HTML document.
///
/// The charset declaration is not cosmetic: the order templates emit "×" and "—", which
/// mojibake in some clients when the html part arrives with no encoding hint. Styling is
/// inline because mail clients strip &lt;style&gt; blocks.
/// </summary>
internal static class EmailLayout
{
    public static string Document(string innerHtml) =>
        "<!doctype html><html lang=\"en\"><head>" +
        "<meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "</head>" +
        "<body style=\"margin:0;padding:24px;background:#ffffff;color:#101418;" +
        "font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:15px;line-height:1.5\">" +
        innerHtml +
        "<hr style=\"border:0;border-top:1px solid #dde2e7;margin:24px 0 12px\">" +
        "<p style=\"margin:0;font-size:12px;color:#6a747d\">" +
        "WidgetWorks &#8212; demo store. This message was generated automatically; no reply is monitored." +
        "</p></body></html>";
}
