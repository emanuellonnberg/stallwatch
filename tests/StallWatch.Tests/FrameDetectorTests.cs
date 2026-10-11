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
