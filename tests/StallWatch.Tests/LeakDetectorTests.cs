using StallWatch.Core.Findings;
using StallWatch.Core.Leaks;

namespace StallWatch.Tests;

public class LeakDetectorTests
{
    static readonly DateTime T0 = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Local);

    static (LeakDetector det, List<Finding> findings) Make(LeakDetectorOptions? o = null)
    {
        var det = new LeakDetector(o ?? new LeakDetectorOptions());
        var findings = new List<Finding>();
        det.FindingRaised += findings.Add;
        return (det, findings);
    }

    static ProcessSample P(int pid, string name, int handles, int threads = 20, long privateMb = 100) =>
        new(pid, name, handles, threads, privateMb * 1024 * 1024);

    // Feeds one snapshot every 30 s for `minutes`, with values from `at(minute)`.
    static void Run(LeakDetector det, double minutes, Func<double, ProcessSample[]> at, double startMin = 0)
    {
        for (var m = startMin; m <= startMin + minutes; m += 0.5)
            det.Observe(T0.AddMinutes(m), at(m));
    }

    [Fact]
    public void Huge_handle_count_is_reported_once()
    {
        var (det, findings) = Make();
        Run(det, 5, _ => [P(14776, "AUEPMaster.exe", 658_000), P(1, "explorer.exe", 5000)]);

        var f = Assert.Single(findings);
        Assert.Equal(FindingKind.ResourceLeak, f.Kind);
        Assert.Contains("AUEPMaster.exe", f.Summary);
        Assert.Contains("658", f.Summary);
    }

    [Fact]
    public void Steady_handle_growth_is_reported_with_rate()
    {
        var (det, findings) = Make();
        // +480 handles/min (the AUEPMaster rate, +8/s), starting small.
        Run(det, 20, m => [P(14776, "AUEPMaster.exe", 2000 + (int)(m * 480))]);

        var f = Assert.Single(findings);
        Assert.Contains("AUEPMaster.exe", f.Summary);
        Assert.Contains("480", f.Summary);
    }

    [Fact]
    public void Noisy_but_flat_process_is_not_a_leak()
    {
        var (det, findings) = Make();
        Run(det, 60, m => [P(7, "chrome.exe", 4000 + (int)(Math.Sin(m) * 1500), threads: 40 + (int)(Math.Cos(m) * 20))]);
        Assert.Empty(findings);
    }

    [Fact]
    public void Short_growth_is_not_a_leak()
    {
        var (det, findings) = Make();
        Run(det, 4, m => [P(7, "setup.exe", 1000 + (int)(m * 2000))]);
        Assert.Empty(findings);
    }

    [Fact]
    public void Thread_growth_is_reported()
    {
        var (det, findings) = Make();
        Run(det, 20, m => [P(44416, "LogiFacecamService.exe", 30_000, threads: 100 + (int)(m * 30))]);

        var f = Assert.Single(findings);
        Assert.Contains("threads", f.Summary);
    }

    [Fact]
    public void Pid_reuse_by_another_program_starts_fresh()
    {
        var (det, findings) = Make();
        Run(det, 8, m => [P(500, "a.exe", 1000 + (int)(m * 400))]);
        Run(det, 8, m => [P(500, "b.exe", 1000 + (int)((m - 8.5) * 400))], startMin: 8.5);
        Assert.Empty(findings);
    }
}
