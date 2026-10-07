using System.Text.Json;
using WidgetWorks.Application.Checkout.PlaceOrder;
using WidgetWorks.Application.Checkout.Reconcile;
using WidgetWorks.Application.Orders.Admin;
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
    public void The_trusted_hop_count_is_written_down_because_it_decides_which_entry_is_believed()
    {
        var shipped = Section("RateLimiting");

        // TrustForwardedFor on its own is not enough. A proxy appends to the header rather than
        // replacing it, so which entry gets read has to be counted from the trusted end — and an
        // operator cannot get that right for their own topology if the setting is invisible.
        // One matches the deployment target: App Service, with nothing in front of it.
        Assert.Equal(new RateLimitOptions().TrustedProxyHops, Number(shipped, "TrustedProxyHops"));
        Assert.Equal(1, new RateLimitOptions().TrustedProxyHops);
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
    public void The_idempotency_settings_in_the_file_match_the_code_defaults()
    {
        var shipped = Section("Idempotency");
        var code = new IdempotencyOptions();

        Assert.Equal(code.RetentionHours, Number(shipped, "RetentionHours"));
        Assert.Equal(code.MaxKeyLength, Number(shipped, "MaxKeyLength"));
    }

    [Fact]
    public void The_reconciliation_settings_in_the_file_match_the_code_defaults()
    {
        var shipped = Section("Reconciliation");
        var code = new ReconciliationOptions();

        Assert.Equal(code.BatchSize, Number(shipped, "BatchSize"));
        Assert.Equal(code.EscalateAfterHours, Number(shipped, "EscalateAfterHours"));

        Assert.True(shipped.TryGetProperty("Enabled", out var enabled));
        Assert.Equal(code.Enabled, enabled.GetBoolean());

        // On by default, and that is deliberate: checkout now parks an unconfirmed charge instead of
        // failing it, which is only safe because something comes back to resolve it. Shipping with
        // this off would leave those orders holding stock for ever.
        Assert.True(code.Enabled);
    }

    [Fact]
    public void An_unreconciled_order_is_escalated_before_a_shopper_would_give_up_on_it()
    {
        var escalate = TimeSpan.FromHours(new ReconciliationOptions().EscalateAfterHours);
        var sweep = TimeSpan.FromMinutes(new ReservationOptions().SweepIntervalMinutes);

        // The sweep has to run several times inside the window, or "unresolved after N hours" would
        // mean "we only looked once". Nothing is decided at escalation — it is a log for a human —
        // but it is the only signal that an order is stuck, so it must not fire on a single bad probe.
        Assert.True(
            escalate > sweep * 2,
            $"escalating after {escalate} gives the {sweep} sweep too few attempts to resolve an order first.");
    }

    [Fact]
    public void The_reconciliation_cadence_is_fast_while_busy_and_cheap_while_quiet()
    {
        var shipped = Section("Reconciliation");
        var code = new ReconciliationOptions();

        Assert.Equal(code.BusyIntervalSeconds, Number(shipped, "BusyIntervalSeconds"));
        Assert.Equal(code.IdleIntervalMinutes, Number(shipped, "IdleIntervalMinutes"));

        // Busy has to be far shorter than idle, or the two settings mean the same thing. The order
        // being chased may already have been charged, so a minute is the right order of magnitude.
        Assert.True(code.BusyIntervalSeconds <= 120, "an unconfirmed charge should be chased within a couple of minutes.");
        Assert.True(
            TimeSpan.FromMinutes(code.IdleIntervalMinutes) > TimeSpan.FromSeconds(code.BusyIntervalSeconds) * 5,
            "the idle wait should be much longer than the busy one; checkout signals the worker directly, so polling is the backstop.");
    }

    [Fact]
    public void The_duplicate_review_window_is_the_span_of_a_mistake_not_of_a_shopping_trip()
    {
        var shipped = Section("OrderReview");
        var code = new OrderReviewOptions();

        Assert.Equal(code.DuplicateWindowMinutes, Number(shipped, "DuplicateWindowMinutes"));
        Assert.Equal(code.Limit, Number(shipped, "Limit"));

        // Wide enough to catch a double-submit, narrow enough not to flag a customer who genuinely
        // ordered the same thing twice in an afternoon. A queue that cries wolf goes unread.
        Assert.InRange(code.DuplicateWindowMinutes, 1, 60);
    }

    [Fact]
    public void Idempotency_keys_outlive_the_sweep_that_forgets_them()
    {
        var retention = TimeSpan.FromHours(new IdempotencyOptions().RetentionHours);
        var sweep = TimeSpan.FromMinutes(new ReservationOptions().SweepIntervalMinutes);

        // Retention shorter than the interval that enforces it would make the window meaningless:
        // keys would survive by however long it took the sweep to come round, not by the figure in
        // the file. It also has to clear any client's retry budget, which a day comfortably does.
        Assert.True(
            retention > sweep,
            "Idempotency keys should be retained for longer than the gap between sweeps.");
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
