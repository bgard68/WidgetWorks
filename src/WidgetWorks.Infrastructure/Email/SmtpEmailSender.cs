using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
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
        using var mail = BuildMailMessage(options, message);

        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.UseStartTls,
            Credentials = string.IsNullOrWhiteSpace(options.Username)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(options.Username, options.Password),
        };

        try
        {
            await client.SendMailAsync(mail, ct);
        }
        catch (Exception ex)
        {
            // Callers treat a notification failure as non-fatal and swallow it, so without this
            // line a misconfigured host is completely invisible: no email and no trace of why.
            Console.WriteLine($"[email] FAILED to send \"{message.Subject}\" to {message.To}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Builds the MIME message. Separated from delivery so its shape can be asserted without an SMTP
    /// server — this is where the bugs actually were: an HTML part that rendered blank, and headers
    /// that mangled non-ASCII subjects.
    /// </summary>
    public static MailMessage BuildMailMessage(EmailOptions options, EmailMessage message)
    {
        var mail = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromName),
            Subject = message.Subject,
            SubjectEncoding = Encoding.UTF8,

            // The plain-text version IS the body, and the HTML version rides alongside as the
            // preferred alternative — the canonical multipart/alternative shape. Leaving Body
            // empty and adding BOTH text and HTML as alternate views produced a message whose
            // HTML part mail clients rendered as blank.
            Body = message.TextBody,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };

        mail.To.Add(message.To);
        mail.AlternateViews.Add(
            AlternateView.CreateAlternateViewFromString(message.HtmlBody, Encoding.UTF8, MediaTypeNames.Text.Html));

        return mail;
    }
}
