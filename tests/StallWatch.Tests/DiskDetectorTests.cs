using StallWatch.Core.Disk;
using StallWatch.Core.Findings;

namespace StallWatch.Tests;

public class DiskDetectorTests
{
    static readonly DateTime T0 = new(2026, 10, 8, 18, 12, 0, DateTimeKind.Local);

    static DiskOp Op(double sec, DiskOpKind kind, double ms = 1, int pid = 100, string proc = "Code.exe",
        string? file = null, int disk = 4) =>
        new(T0.AddSeconds(sec), disk, kind, ms, pid, proc, file, kind == DiskOpKind.Flush ? 0 : 4096);

    static (DiskDetector det, List<Finding> findings, List<DiskSecond> seconds) Make(DiskDetectorOptions? o = null)
    {
        var det = new DiskDetector(o ?? new DiskDetectorOptions());
        var findings = new List<Finding>();
        var seconds = new List<DiskSecond>();
        det.FindingRaised += findings.Add;
        det.SecondClosed += seconds.Add;
        return (det, findings, seconds);
    }

    // Emits `perSec` flushes per second for `secs` seconds, each preceded by a write to `file`.
    static void Storm(DiskDetector det, double start, int secs, int perSec, string proc = "Code.exe", int pid = 100,
        string file = @"C:\Users\x\codebase-external.sqlite")
    {
        for (var s = 0; s < secs; s++)
            for (var i = 0; i < perSec; i++)
            {
                var t = start + s + i / (double)perSec;
                det.Add(Op(t, DiskOpKind.Write, pid: pid, proc: proc, file: file));
                det.Add(Op(t + 0.0001, DiskOpKind.Flush, pid: pid, proc: proc));
            }
    }

    [Fact]
    public void Single_slow_io_raises_one_stall_naming_process_and_latency()
    {
        var (det, findings, _) = Make();
        det.Add(Op(0.1, DiskOpKind.Read, ms: 2));
        det.Add(Op(0.5, DiskOpKind.Flush, ms: 600, proc: "System", pid: 4));
        det.Add(Op(0.6, DiskOpKind.Flush, ms: 580, proc: "chrome.exe", pid: 7));
        det.Tick(T0.AddSeconds(2));

        var stall = Assert.Single(findings);
        Assert.Equal(FindingKind.DiskStall, stall.Kind);
        Assert.Contains("600", stall.Summary);
        Assert.Contains("disk 4", stall.Summary);
    }

    [Fact]
    public void Fast_io_raises_nothing()
    {
        var (det, findings, _) = Make();
        for (var i = 0; i < 1000; i++) det.Add(Op(i * 0.01, DiskOpKind.Write, ms: 0.2));
        det.Tick(T0.AddSeconds(20));
        Assert.Empty(findings);
    }

    [Fact]
    public void Sustained_flush_storm_raises_exactly_one_finding_with_process_and_file()
    {
        var (det, findings, _) = Make();
        Storm(det, 0, secs: 15, perSec: 200);
        det.Tick(T0.AddSeconds(20));

        var f = Assert.Single(findings, x => x.Kind == FindingKind.FlushStorm);
        Assert.Contains("Code.exe", f.Summary);
        Assert.Contains(f.Evidence, e => e.Contains("codebase-external.sqlite"));
    }

    [Fact]
    public void Flushing_below_threshold_is_not_a_storm()
    {
        var (det, findings, _) = Make();
        Storm(det, 0, secs: 30, perSec: 15);
        det.Tick(T0.AddSeconds(40));
        Assert.DoesNotContain(findings, x => x.Kind == FindingKind.FlushStorm);
    }

    [Fact]
    public void Short_burst_is_not_a_storm()
    {
        var (det, findings, _) = Make();
        Storm(det, 0, secs: 5, perSec: 300);
        det.Tick(T0.AddSeconds(20));
        Assert.DoesNotContain(findings, x => x.Kind == FindingKind.FlushStorm);
    }

    [Fact]
    public void Storm_rearms_after_it_stops()
    {
        var (det, findings, _) = Make();
        Storm(det, 0, secs: 12, perSec: 100);
        Storm(det, 30, secs: 12, perSec: 100);
        det.Tick(T0.AddSeconds(50));
        Assert.Equal(2, findings.Count(x => x.Kind == FindingKind.FlushStorm));
    }

    [Fact]
    public void Second_stats_aggregate_per_disk()
    {
        var (det, _, seconds) = Make();
        det.Add(Op(0.1, DiskOpKind.Read, ms: 3));
        det.Add(Op(0.2, DiskOpKind.Write, ms: 9));
        det.Add(Op(0.3, DiskOpKind.Flush, ms: 40));
        det.Add(Op(0.4, DiskOpKind.Read, ms: 1, disk: 0));
        det.Tick(T0.AddSeconds(2));

        var c = Assert.Single(seconds, s => s.Disk == 4);
        Assert.Equal(1, c.Reads);
        Assert.Equal(1, c.Writes);
        Assert.Equal(1, c.Flushes);
        Assert.Equal(40, c.MaxMs);
        Assert.Single(seconds, s => s.Disk == 0);
    }
}
