using WidgetWorks.Application.Abstractions;

namespace WidgetWorks.Infrastructure.Email;

/// <summary>
/// Development email sender: writes messages to stdout so they're visible in the app log (or a local
/// mail catcher's absence) without a real SMTP server. Not for production — use SmtpEmailSender there.
/// </summary>
public sealed class DevEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Console.WriteLine($"[email] To: {message.To} | Subject: {message.Subject}");
        Console.WriteLine(message.TextBody);
        return Task.CompletedTask;
    }
}
