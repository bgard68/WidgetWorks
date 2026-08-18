using System.Net;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Application.Notifications;

/// <summary>Builds the account-related transactional emails (welcome, password reset).</summary>
public static class AccountEmailTemplates
{
    public static EmailMessage Welcome(string email) => new(
        email,
        "Welcome to WidgetWorks",
        EmailLayout.Document("<p>Welcome to WidgetWorks! Your account is ready &#8212; happy widgeting.</p>"),
        "Welcome to WidgetWorks! Your account is ready - happy widgeting.");

    public static EmailMessage PasswordReset(string email, string link) => new(
        email,
        "Reset your WidgetWorks password",
        EmailLayout.Document(
            "<p>We received a request to reset your WidgetWorks password.</p>" +
            // A tappable button, plus the raw URL beneath it — clients that strip the anchor
            // or forward as plain text still leave the recipient a usable link.
            $"<p><a href=\"{WebUtility.HtmlEncode(link)}\" " +
            "style=\"display:inline-block;padding:12px 22px;border-radius:999px;background:#ffd814;" +
            "border:1px solid #e5bd00;color:#0f1111;font-weight:600;text-decoration:none\">" +
            "Reset your password</a></p>" +
            "<p style=\"font-size:13px;color:#6a747d;word-break:break-all\">" +
            $"Or paste this link into your browser:<br>{WebUtility.HtmlEncode(link)}</p>" +
            "<p>This link expires in 30 minutes. If you didn't request this, you can safely " +
            "ignore this email &#8212; your password will not change.</p>"),
        $"Reset your WidgetWorks password: {link}\nThis link expires in 30 minutes. " +
        "If you didn't request this, you can safely ignore this email.");
}
