namespace WidgetWorks.Application.Abstractions;

public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>Sends transactional email. Real SMTP in production; a dev sender writes to the log.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}
