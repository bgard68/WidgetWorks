using WidgetWorks.Domain.Common;
using Xunit;

namespace WidgetWorks.UnitTests;

public class ClockSampleTests
{
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void TimeProvider_is_deterministic_when_injected()
    {
        var instant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        TimeProvider clock = new FixedTimeProvider(instant);

        Assert.Equal(instant, clock.GetUtcNow());
    }

    [Fact]
    public void Result_failure_carries_error()
    {
        var result = Result.Fail("nope");

        Assert.True(result.IsFailure);
        Assert.Equal("nope", result.Error);
    }
}
