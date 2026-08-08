namespace WidgetWorks.Application;

/// <summary>App-level settings, e.g. the public base URL used to build links in emails.</summary>
public sealed class AppOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5173";
}
