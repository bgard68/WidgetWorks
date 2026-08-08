using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Application.Notifications;

/// <summary>Builds the account-related transactional emails (welcome, password reset).</summary>
public static class AccountEmailTemplates
{
    public static EmailMessage Welcome(string email) => new(
        email,
        "Welcome to WidgetWorks",
        "<p>Welcome to WidgetWorks! Your account is ready — happy widgeting.</p>",
        "Welcome to WidgetWorks! Your account is ready - happy widgeting.");

    public static EmailMessage PasswordReset(string email, string link) => new(
        email,
        "Reset your WidgetWorks password",
        $"<p>We received a request to reset your password. <a href=\"{link}\">Reset it here</a>. " +
        "This link expires in 30 minutes. If you didn't request this, you can safely ignore this email.</p>",
        $"Reset your WidgetWorks password: {link}\nThis link expires in 30 minutes. " +
        "If you didn't request this, you can safely ignore this email.");
}
