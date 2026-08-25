using System.Text.Json;
using WidgetWorks.Application.Checkout.ReleaseStale;
using WidgetWorks.WebApi.RateLimiting;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// Keeps the shipped appsettings.json honest.
///
/// Writing these settings into the file made them discoverable — an operator can now see that
/// throttling budgets and the reservation sweep are tunable at all, and that TrustForwardedFor
/// exists, which matters because getting it wrong turns per-caller throttling into a global cap.
///
/// The cost of that is two sources of truth. If the file and the code defaults drift, the file
/// starts describing an application that no longer behaves that way, which is worse than not
/// documenting the setting at all. These tests are the guard: change a default in code without the
/// file, or the file without the code, and they fail.
/// </summary>
public class ShippedConfigurationTests
{
    private static JsonElement Section(string name)
    {
        var json = JsonDocument.Parse(File.ReadAllText(AppSettingsPath()));
        Assert.True(
            json.RootElement.TryGetProperty(name, out var section),
            $"appsettings.json has no '{name}' section, so the settings it controls are invisible to anyone deploying this.");
        return section.Clone();
    }

    /// <summary>
    /// Walks up from the test binary to the repository root, identified by the solution file, so
    /// this resolves the same way locally and on a build agent.
    /// </summary>
    private static string AppSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WidgetWorks.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "WidgetWorks.WebApi", "appsettings.json");
        Assert.True(File.Exists(path), $"Expected appsettings.json at {path}.");
        return path;
    }

    private static int Number(JsonElement section, params string[] path)
    {
        var current = section;
        foreach (var step in path)
        {
            Assert.True(current.TryGetProperty(step, out current), $"Missing '{string.Join(':', path)}'.");
        }

        return current.GetInt32();
    }

    [Fact]
    public void The_throttling_budgets_in_the_file_match_the_code_defaults()
    {
        var shipped = Section("RateLimiting");
        var code = new RateLimitOptions();

        Assert.Equal(code.Auth.PermitLimit, Number(shipped, "Auth", "PermitLimit"));
        Assert.Equal(code.Auth.WindowSeconds, Number(shipped, "Auth", "WindowSeconds"));
        Assert.Equal(code.Checkout.PermitLimit, Number(shipped, "Checkout", "PermitLimit"));
        Assert.Equal(code.Checkout.WindowSeconds, Number(shipped, "Checkout", "WindowSeconds"));
        Assert.Equal(code.Lookup.PermitLimit, Number(shipped, "Lookup", "PermitLimit"));
        Assert.Equal(code.Lookup.WindowSeconds, Number(shipped, "Lookup", "WindowSeconds"));
    }

    [Fact]
    public void The_shipped_default_does_not_trust_a_forwarded_header()
    {
        var shipped = Section("RateLimiting");

        Assert.True(shipped.TryGetProperty("TrustForwardedFor", out var trust));
        // False is the safe default: believing the header with no proxy in front lets a caller forge
        // it and give itself unlimited throttling partitions. A deployment behind a proxy must turn
        // it on deliberately, which is why it is written here rather than left implicit.
        Assert.False(trust.GetBoolean());
        Assert.False(new RateLimitOptions().TrustForwardedFor);
    }

    [Fact]
    public void The_reservation_sweep_settings_in_the_file_match_the_code_defaults()
    {
        var shipped = Section("Reservations");
        var code = new ReservationOptions();

        Assert.Equal(code.ExpireAfterMinutes, Number(shipped, "ExpireAfterMinutes"));
        Assert.Equal(code.SweepIntervalMinutes, Number(shipped, "SweepIntervalMinutes"));
        Assert.Equal(code.BatchSize, Number(shipped, "BatchSize"));

        Assert.True(shipped.TryGetProperty("Enabled", out var enabled));
        Assert.Equal(code.Enabled, enabled.GetBoolean());
    }

    [Fact]
    public void The_sweep_window_is_longer_than_the_interval_that_checks_it()
    {
        var code = new ReservationOptions();

        // A window shorter than the sweep interval would mean orders sit expired but unreleased for
        // most of their life, which quietly defeats the point of having a sweep.
        Assert.True(
            code.ExpireAfterMinutes > code.SweepIntervalMinutes,
            "Reservations should expire over a longer span than the interval that looks for them.");
    }
}
