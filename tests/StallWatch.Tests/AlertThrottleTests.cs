using StallWatch.Core.Findings;
using StallWatch.Core.Monitoring;

namespace StallWatch.Tests;

public class AlertThrottleTests
{
    static readonly DateTime T0 = new(2026, 10, 11, 20, 0, 0, DateTimeKind.Local);
    static Finding F(double minutes, FindingKind kind) => new(T0.AddMinutes(minutes), kind, "x", []);

    [Fact]
    public void Same_kind_is_quiet_for_five_minutes()
    {
        var t = new AlertThrottle();
        Assert.True(t.Allow(F(0, FindingKind.FlushStorm)));
        Assert.False(t.Allow(F(2, FindingKind.FlushStorm)));
        Assert.False(t.Allow(F(4.9, FindingKind.FlushStorm)));
        Assert.True(t.Allow(F(5, FindingKind.FlushStorm)));
    }

    [Fact]
    public void Different_kinds_still_wait_one_minute()
    {
        var t = new AlertThrottle();
        Assert.True(t.Allow(F(0, FindingKind.FlushStorm)));
        Assert.False(t.Allow(F(0.5, FindingKind.FrameFreeze)));
        Assert.True(t.Allow(F(1, FindingKind.FrameFreeze)));
    }

    [Fact]
    public void Suppressed_alerts_do_not_extend_the_quiet_period()
    {
        var t = new AlertThrottle();
        Assert.True(t.Allow(F(0, FindingKind.InputDelay)));
        Assert.False(t.Allow(F(4, FindingKind.InputDelay)));
        Assert.True(t.Allow(F(5, FindingKind.InputDelay)));
    }
}
