using StallWatch.Core.Disk;
using StallWatch.Core.Findings;
using StallWatch.Core.Frames;

namespace StallWatch.Tests;

public class FrameDetectorTests
{
    static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Local);

    static (FrameDetector det, List<Finding> findings) Make(DiskDetector? disk = null, string? app = null)
    {
        var det = new FrameDetector(new FrameDetectorOptions { App = app }, disk);
        var findings = new List<Finding>();
        det.FindingRaised += findings.Add;
        return (det, findings);
    }

    // Presents at `fps` from `start` for `seconds`; returns time of the last present.
    static double Present(FrameDetector det, double start, double seconds, double fps = 120, int pid = 94052, string name = "DTS.exe")
    {
        var t = start;
        for (; t < start + seconds; t += 1 / fps) det.Present(pid, name, T0.AddSeconds(t));
        return t - 1 / fps;
    }

    [Fact]
    public void Smooth_presenting_raises_nothing()
    {
        var (det, findings) = Make();
        Present(det, 0, 10);
        Assert.Empty(findings);
    }

    [Fact]
    public void Gap_in_steady_presenting_is_a_freeze_with_duration()
    {
        var (det, findings) = Make();
        var last = Present(det, 0, 5);
        Present(det, last + 0.8, 2);

        var f = Assert.Single(findings);
        Assert.Equal(FindingKind.FrameFreeze, f.Kind);
        Assert.Contains("DTS.exe", f.Summary);
        Assert.Contains("800", f.Summary);
    }

    [Fact]
    public void App_that_was_not_presenting_steadily_is_ignored()
    {
        var (det, findings) = Make();
        // A UI that presents only when something changes: 2 fps.
        Present(det, 0, 10, fps: 2, pid: 7, name: "explorer.exe");
        Assert.Empty(findings);
    }

    [Fact]
    public void Very_long_gap_is_treated_as_minimized_not_frozen()
    {
        var (det, findings) = Make();
        var last = Present(det, 0, 5);
        Present(det, last + 30, 2);
        Assert.Empty(findings);
    }

    [Fact]
    public void App_filter_limits_reports_to_that_app()
    {
        var (det, findings) = Make(app: "DTS.exe");
        var last = Present(det, 0, 5, pid: 1, name: "chrome.exe");
        Present(det, last + 0.8, 1, pid: 1, name: "chrome.exe");
        Assert.Empty(findings);
    }

    [Fact]
    public void Without_app_filter_only_the_foreground_app_is_reported()
    {
        var det = new FrameDetector(new FrameDetectorOptions { IsForeground = pid => pid == 94052 });
        var findings = new List<Finding>();
        det.FindingRaised += findings.Add;

        var last = Present(det, 0, 5, pid: 1, name: "chrome.exe");
        Present(det, last + 0.4, 1, pid: 1, name: "chrome.exe");
        last = Present(det, 0, 5);
        Present(det, last + 0.4, 1);

        Assert.Contains("DTS.exe", Assert.Single(findings).Summary);
    }

    [Fact]
    public void Freeze_includes_disk_evidence_from_the_same_moment()
    {
        var disk = new DiskDetector(new DiskDetectorOptions()) { DiskName = d => d == 4 ? "C: (Samsung SSD 980 PRO 2TB)" : $"disk {d}" };
        var (det, findings) = Make(disk);
        var last = Present(det, 0, 5);
        // During the gap: Code.exe flushes C:, one flush takes 700 ms.
        for (var i = 0; i < 50; i++)
            disk.Add(new DiskOp(T0.AddSeconds(last + 0.01 * i), 4, DiskOpKind.Flush, i == 40 ? 700 : 2, 7872, "Code.exe", null, 0));
        Present(det, last + 0.9, 1);

        var f = Assert.Single(findings);
        Assert.Contains(f.Evidence, e => e.Contains("C: (Samsung SSD 980 PRO 2TB)") && e.Contains("700"));
        Assert.Contains(f.Evidence, e => e.Contains("Code.exe"));
    }
}

public class FrameStutterTests
{
    static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Local);

    static List<Finding> Run(Func<int, double> intervalMs, double seconds)
    {
        var det = new FrameDetector(new FrameDetectorOptions { App = "game.exe" });
        var findings = new List<Finding>();
        det.FindingRaised += findings.Add;
        var t = 0.0;
        for (var i = 0; t < seconds * 1000; i++)
        {
            det.Present(1, "game.exe", T0.AddMilliseconds(t));
            t += intervalMs(i);
        }
        return findings;
    }

    [Fact]
    public void Even_frames_are_not_stutter()
    {
        Assert.Empty(Run(_ => 8.33, 30));
    }

    [Fact]
    public void Every_tenth_frame_late_is_reported_as_stutter_not_freeze()
    {
        var findings = Run(i => i % 10 == 9 ? 45 : 8.33, 30);
        Assert.Contains(findings, f => f.Kind == FindingKind.FrameStutter && f.Summary.Contains("game.exe"));
        Assert.DoesNotContain(findings, f => f.Kind == FindingKind.FrameFreeze);
    }

    static List<Finding> RunAnyApp(Func<int, double> intervalMs, double seconds, bool foreground)
    {
        var det = new FrameDetector(new FrameDetectorOptions { IsForeground = _ => foreground });
        var findings = new List<Finding>();
        det.FindingRaised += findings.Add;
        var t = 0.0;
        for (var i = 0; t < seconds * 1000; i++)
        {
            det.Present(1, "FortniteClient-Win64-Shipping.exe", T0.AddMilliseconds(t));
            t += intervalMs(i);
        }
        return findings;
    }

    [Fact]
    public void Foreground_game_is_checked_for_stutter_without_app_filter()
    {
        var findings = RunAnyApp(i => i % 10 == 9 ? 45 : 8.33, 30, foreground: true);
        Assert.Contains(findings, f => f.Kind == FindingKind.FrameStutter && f.Summary.Contains("Fortnite"));
    }

    [Fact]
    public void Background_or_slow_apps_are_not_checked_for_stutter()
    {
        Assert.Empty(RunAnyApp(i => i % 10 == 9 ? 45 : 8.33, 30, foreground: false));
        // ~22 fps with hitches: below the 30 fps a game would hold.
        Assert.DoesNotContain(RunAnyApp(i => i % 10 == 9 ? 120 : 40, 30, foreground: true), f => f.Kind == FindingKind.FrameStutter);
    }

    [Fact]
    public void Stutter_is_rate_limited()
    {
        var findings = Run(i => i % 10 == 9 ? 45 : 8.33, 50);
        Assert.Single(findings, f => f.Kind == FindingKind.FrameStutter);
    }
}
