using StallWatch.Core.Findings;
using StallWatch.Core.Input;

namespace StallWatch.Tests;

public class InputDetectorTests
{
    static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Local);

    static (InputDetector det, List<Finding> findings) Make()
    {
        var det = new InputDetector(new InputDetectorOptions());
        var findings = new List<Finding>();
        det.FindingRaised += findings.Add;
        return (det, findings);
    }

    [Fact]
    public void Normal_input_is_quiet()
    {
        var (det, findings) = Make();
        for (var i = 0; i < 3000; i++) det.Add(T0.AddMilliseconds(i * 20), InputKind.Mouse, i % 7 == 0 ? 16 : 0);
        det.Tick(T0.AddMinutes(2));
        Assert.Empty(findings);
    }

    [Fact]
    public void Repeated_slow_delivery_is_reported_once_with_numbers()
    {
        var (det, findings) = Make();
        // The slowhook pattern: every 10th event ~80-110 ms late, for 30 s.
        for (var i = 0; i < 1500; i++)
            det.Add(T0.AddMilliseconds(i * 20), i % 2 == 0 ? InputKind.Mouse : InputKind.Keyboard, i % 10 == 9 ? 80 + i % 30 : 0);
        det.Tick(T0.AddMinutes(1));

        var f = Assert.Single(findings);
        Assert.Equal(FindingKind.InputDelay, f.Kind);
        Assert.Contains("mouse", f.Summary);
    }

    [Fact]
    public void One_huge_delay_is_enough()
    {
        var (det, findings) = Make();
        det.Add(T0, InputKind.Keyboard, 0);
        det.Add(T0.AddMilliseconds(100), InputKind.Keyboard, 1125);
        det.Tick(T0.AddSeconds(20));
        Assert.Single(findings);
        Assert.Contains("1125", findings[0].Summary);
    }

    [Fact]
    public void A_single_moderate_blip_is_ignored()
    {
        var (det, findings) = Make();
        det.Add(T0, InputKind.Mouse, 120);
        for (var i = 1; i < 500; i++) det.Add(T0.AddMilliseconds(i * 20), InputKind.Mouse, 0);
        det.Tick(T0.AddSeconds(30));
        Assert.Empty(findings);
    }
}
