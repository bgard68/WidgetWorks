using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using WidgetWorks.WebApi.RateLimiting;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// The watcher for the configuration mistake that turns per-caller throttling into a global cap.
/// Both directions of the mistake are silent in production, so the warning is the whole feature and
/// deserves to be pinned.
/// </summary>
public class ProxyConfigurationCheckTests
{
    /// <summary>Captures warnings so the test can assert on what an operator would actually see.</summary>
    private sealed class CapturingLogger : ILogger<ProxyConfigurationCheck>
    {
        public readonly List<string> Warnings = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private static HttpContext Request(bool withForwardedFor)
    {
        var context = new DefaultHttpContext();
        if (withForwardedFor)
        {
            context.Request.Headers["X-Forwarded-For"] = "198.51.100.9";
        }

        return context;
    }

    [Fact]
    public void Warns_when_a_proxy_is_in_front_but_its_header_is_not_trusted()
    {
        var log = new CapturingLogger();
        var check = new ProxyConfigurationCheck(new RateLimitOptions { TrustForwardedFor = false }, log);

        check.Inspect(Request(withForwardedFor: true));

        // This is the outage case: every caller collapses into one partition and the limits start
        // applying to all traffic combined.
        Assert.Single(log.Warnings);
        Assert.Contains("one throttling partition", log.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Warns_when_the_header_is_trusted_but_no_proxy_appears_to_send_it()
    {
        var log = new CapturingLogger();
        var check = new ProxyConfigurationCheck(new RateLimitOptions { TrustForwardedFor = true }, log);

        check.Inspect(Request(withForwardedFor: false));

        // The inverse, and the security half: a caller can forge the header and opt out of limits.
        Assert.Single(log.Warnings);
        Assert.Contains("forge", log.Warnings[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Stays_quiet_when_the_setting_matches_the_traffic(bool trust, bool forwarded)
    {
        var log = new CapturingLogger();
        var check = new ProxyConfigurationCheck(new RateLimitOptions { TrustForwardedFor = trust }, log);

        check.Inspect(Request(forwarded));

        Assert.Empty(log.Warnings);
    }

    [Fact]
    public void Warns_once_however_much_traffic_arrives()
    {
        var log = new CapturingLogger();
        var check = new ProxyConfigurationCheck(new RateLimitOptions { TrustForwardedFor = false }, log);

        for (var i = 0; i < 50; i++)
        {
            check.Inspect(Request(withForwardedFor: true));
        }

        // A page of identical warnings is how a real signal gets scrolled past.
        Assert.Single(log.Warnings);
    }
}
