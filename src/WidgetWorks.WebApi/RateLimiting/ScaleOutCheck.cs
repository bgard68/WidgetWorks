namespace WidgetWorks.WebApi.RateLimiting;

/// <summary>
/// Says so when the deployment outgrows the assumption the limiter is built on.
///
/// Throttling counters live in this process's memory. That is correct while exactly one instance
/// serves traffic — and wrong the moment a second one does, because each keeps its own counters and
/// enforces the configured budget separately. Two instances double every limit, four quadruple it.
///
/// Nothing about that surfaces on its own: no error, no rejected request, no log line. The limits
/// simply stop meaning what they say, which is the same silent-failure shape
/// <see cref="ProxyConfigurationCheck"/> exists for — and the reason both of these are checks rather
/// than comments.
/// </summary>
public static class ScaleOutCheck
{
    /// <summary>
    /// App Service tiers that cannot run more than one instance, so in-memory counters are the whole
    /// picture. Every other tier can scale out, whether or not it currently has.
    /// </summary>
    private static readonly string[] SingleInstanceTiers = ["Free", "Shared"];

    /// <summary>
    /// Warns when <paramref name="websiteSku"/> names a tier that can run several instances.
    ///
    /// The tier is the only signal available: an instance cannot see how many siblings it has, so
    /// this reports "this deployment is able to scale out" rather than "it has". That is the useful
    /// warning anyway — on an autoscaling plan the second instance can arrive at any moment, and the
    /// limits would already be wrong by the time anyone noticed.
    /// </summary>
    /// <param name="websiteSku">
    /// The <c>WEBSITE_SKU</c> environment variable. Absent off App Service — local runs and
    /// containers — where there is no platform claim to check and this stays quiet.
    /// </param>
    /// <returns>The warning that was logged, or null when the tier is single-instance.</returns>
    public static string? Inspect(string? websiteSku, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);

        if (string.IsNullOrWhiteSpace(websiteSku) ||
            SingleInstanceTiers.Contains(websiteSku.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var message =
            $"Rate limiting keeps its counters in this instance's memory, but WEBSITE_SKU is " +
            $"'{Diagnostics.LogSafe.Text(websiteSku, maxLength: 32)}', a tier that can run more than one " +
            "instance. Each instance would enforce the configured budgets separately, so every limit " +
            "is multiplied by the instance count. Move to a shared counter store before scaling out, " +
            "or divide the budgets by the instance count and accept the imprecision.";

        warn(message);
        return message;
    }
}
