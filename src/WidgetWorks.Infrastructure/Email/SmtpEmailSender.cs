using System.Net;
using System.Net.Mail;
using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Email;

/// <summary>
/// Real SMTP delivery via System.Net.Mail. Works against any SMTP host (SendGrid, Mailgun, Postmark,
/// Amazon SES, or a local Mailpit/MailHog catcher). Host/credentials come only from configuration /
/// user-secrets and are never committed. See ADR-023 for the production upgrade path (MailKit).
/// </summary>
public sealed class SmtpEmailSender(EmailOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        using var mail = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromName),
            Subject = message.Subject,
        };
        mail.To.Add(message.To);
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.TextBody, null, "text/plain"));
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.HtmlBody, null, "text/html"));

        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.UseStartTls,
            Credentials = string.IsNullOrWhiteSpace(options.Username)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(options.Username, options.Password),
        };

        await client.SendMailAsync(mail, ct);
    }
}
