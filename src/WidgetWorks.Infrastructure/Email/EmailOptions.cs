namespace WidgetWorks.Infrastructure.Email;

public sealed class EmailOptions
{
    /// <summary>"Smtp" for real delivery; anything else (default) uses the dev sender that logs to stdout.</summary>
    public string Provider { get; set; } = "Dev";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 587;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool UseStartTls { get; set; } = true;

    public string FromAddress { get; set; } = "no-reply@widgetworks.demo";

    public string FromName { get; set; } = "WidgetWorks";
}
