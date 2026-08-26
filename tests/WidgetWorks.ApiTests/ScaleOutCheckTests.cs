using WidgetWorks.WebApi.RateLimiting;
using Xunit;

namespace WidgetWorks.ApiTests;

/// <summary>
/// The guard on the limiter's one unstated assumption: that a single instance serves traffic.
///
/// Worth testing rather than trusting because the condition it watches for produces no symptom.
/// A second instance does not error, retry, or log — it just enforces the same budget again,
/// independently, and the numbers in appsettings.json quietly stop being the numbers in effect.
/// </summary>
public class ScaleOutCheckTests
{
    private static (string? Warning, int Count) Inspect(string? sku)
    {
        string? captured = null;
        var count = 0;
        ScaleOutCheck.Inspect(sku, message => { captured = message; count++; });
        return (captured, count);
    }

    [Theory]
    [InlineData("Free")]
    [InlineData("Shared")]
    public void A_single_instance_tier_is_what_the_limiter_assumes_and_says_nothing(string sku)
        => Assert.Equal((null, 0), Inspect(sku));

    [Theory]
    [InlineData("free")]
    [InlineData("FREE")]
    [InlineData("  Free  ")]
    public void The_tier_is_matched_regardless_of_how_the_platform_cases_it(string sku)
        => Assert.Null(Inspect(sku).Warning);

    [Theory]
    [InlineData("Basic")]
    [InlineData("Standard")]
    [InlineData("Premium")]
    [InlineData("PremiumV3")]
    [InlineData("Isolated")]
    public void A_tier_that_can_scale_out_is_warned_about(string sku)
    {
        var (warning, count) = Inspect(sku);

        Assert.Equal(1, count);
        Assert.Contains(sku, warning!, StringComparison.Ordinal);
        // The warning has to say what to do about it, or it is just noise in a log nobody reads.
        Assert.Contains("multiplied by the instance count", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Off_App_Service_there_is_no_platform_claim_to_check(string? sku)
    {
        // Local runs and containers do not set WEBSITE_SKU. Warning there would train an operator
        // to ignore the message, which costs more than the message is worth.
        Assert.Equal((null, 0), Inspect(sku));
    }

    [Fact]
    public void An_unrecognised_tier_warns_rather_than_assuming_it_is_safe()
    {
        // A tier this code has never heard of is likelier to be new and scalable than new and
        // single-instance, and the wrong guess is the silent one.
        Assert.NotNull(Inspect("SomeTierInventedNextYear").Warning);
    }

    [Fact]
    public void A_hostile_tier_value_cannot_forge_a_log_entry()
    {
        // WEBSITE_SKU comes from the environment, and an environment variable is not automatically
        // trustworthy text — the same CWE-117 reasoning applied to the request path.
        var warning = Inspect("Standard\r\nfatal: database deleted by admin").Warning;

        Assert.NotNull(warning);
        Assert.DoesNotContain('\n', warning);
        Assert.DoesNotContain('\r', warning);
    }

    [Fact]
    public void A_missing_sink_is_a_programming_error_not_a_silent_pass()
        => Assert.Throws<ArgumentNullException>(() => ScaleOutCheck.Inspect("Standard", null!));
}
