using StallWatch.Core.Causes;
using StallWatch.Core.Findings;
using StallWatch.Core.Frames;

namespace StallWatch.Tests;

public class CauseAnalyzerTests
{
    static readonly DateTime T0 = new(2026, 10, 11, 21, 0, 0, DateTimeKind.Local);
    const int Game = 52316;
    const string GameName = "FortniteClient-Win64-Shipping.exe";
    const string Gpu = "NVIDIA GeForce RTX 5090";

    // A calm machine for 60 s, then a freeze from 60.0 to 60.6 s.
    static CauseAnalyzer Calm(Action<CauseAnalyzer, DateTime>? during = null)
    {
        var a = new CauseAnalyzer();
        for (var t = 0.5; t <= 62; t += 0.5)
        {
            var now = T0.AddSeconds(t);
            a.Add(new CpuSample(now, 25, 80, 5, 0, 12_000, 70));
            if (t % 2 == 0) a.Add(new ProcessCpuSample(now, TimeSpan.FromSeconds(2), 5, [new(Game, GameName, 1.0, 0), new(100, "chrome.exe", 0.5, 0)]));
            if (t % 1 == 0)
                a.Add(new GpuSample(now, [new AdapterLoad(Gpu, 40, 40, "3D", 8_000, 32_000)],
                    new Dictionary<int, ProcessGpu> { [Game] = new(Gpu, 38) }));
        }
        during?.Invoke(a, T0.AddSeconds(60));
        return a;
    }

    static CauseReport Explain(CauseAnalyzer a) => a.Explain(Game, GameName, T0.AddSeconds(60), T0.AddSeconds(60.6));

    [Fact]
    public void Calm_machine_gives_no_verdict_but_context()
    {
        var r = Explain(Calm());
        Assert.Null(r.Likely);
        Assert.Contains(r.Evidence, e => e.StartsWith("At that moment: CPU 25%") && e.Contains(Gpu));
    }

    [Fact]
    public void Long_driver_call_is_the_verdict()
    {
        var r = Explain(Calm((a, t) => a.Add(new DriverRun(t.AddMilliseconds(300), "nvlddmkm.sys", 14, false, 0))));
        Assert.Equal("the driver nvlddmkm.sys held CPU 0 for 14 ms", r.Likely);
    }

    [Fact]
    public void Short_driver_calls_are_only_evidence()
    {
        var r = Explain(Calm((a, t) => a.Add(new DriverRun(t.AddMilliseconds(300), "dxgkrnl.sys", 2.5, false, 3))));
        Assert.Null(r.Likely);
        Assert.Contains(r.Evidence, e => e.Contains("dxgkrnl.sys") && e.Contains("2.5 ms"));
    }

    [Fact]
    public void Full_video_memory_is_named()
    {
        var r = Explain(Calm((a, t) => a.Add(new GpuSample(t.AddSeconds(0.5), [new AdapterLoad(Gpu, 60, 60, "3D", 31_500, 32_000)],
            new Dictionary<int, ProcessGpu> { [Game] = new(Gpu, 55) }))));
        Assert.Equal($"graphics memory on {Gpu} was full", r.Likely);
    }

    [Fact]
    public void The_game_itself_getting_busy_points_at_the_game()
    {
        var r = Explain(Calm((a, t) => a.Add(new ProcessCpuSample(t.AddSeconds(0.5), TimeSpan.FromSeconds(2), 5,
            [new(Game, GameName, 6.5, 0), new(100, "chrome.exe", 0.5, 0)]))));
        Assert.StartsWith(GameName + " itself was busy", r.Likely);
    }

    [Fact]
    public void Another_process_spiking_is_named()
    {
        var r = Explain(Calm((a, t) => a.Add(new ProcessCpuSample(t.AddSeconds(0.5), TimeSpan.FromSeconds(2), 5,
            [new(Game, GameName, 1.0, 0), new(200, "MsMpEng.exe", 8, 0)]))));
        Assert.Equal("MsMpEng.exe suddenly used 8 CPU cores", r.Likely);
    }

    [Fact]
    public void Paging_beats_cpu()
    {
        var r = Explain(Calm((a, t) =>
        {
            a.Add(new CpuSample(t.AddSeconds(0.5), 99, 100, 5, 0, 300, 98));
            a.Add(new ProcessCpuSample(t.AddSeconds(0.5), TimeSpan.FromSeconds(2), 2400, [new(Game, GameName, 1.0, 900)]));
        }));
        Assert.Equal("memory was nearly full (300 MB free)", r.Likely);
        Assert.Contains(r.Evidence, e => e.Contains("hard page faults") && e.Contains("900 of them in " + GameName));
    }

    [Fact]
    public void Freeze_finding_carries_the_verdict()
    {
        var a = Calm((an, t) => an.Add(new DriverRun(t.AddMilliseconds(300), "nvlddmkm.sys", 14, false, 0)));
        var det = new FrameDetector(new FrameDetectorOptions { App = GameName, Explain = a.Explain });
        var findings = new List<Finding>();
        det.FindingRaised += findings.Add;
        for (var t = 55.0; t < 60; t += 1 / 120.0) det.Present(Game, GameName, T0.AddSeconds(t));
        det.Present(Game, GameName, T0.AddSeconds(60.6));

        var f = Assert.Single(findings);
        Assert.Contains("Likely cause: the driver nvlddmkm.sys held CPU 0 for 14 ms.", f.Summary);
    }
}

public class NetworkDetectorTests
{
    static readonly DateTime T0 = new(2026, 10, 11, 21, 0, 0, DateTimeKind.Local);
    const string Router = "router (192.168.1.1)", Internet = "internet (1.1.1.1)";

    static List<Finding> Run(Func<int, double?> router, Func<int, double?> internet, int seconds = 30)
    {
        var d = new NetworkDetector();
        var findings = new List<Finding>();
        d.FindingRaised += findings.Add;
        for (var i = 0; i < seconds; i++)
        {
            d.Add(new PingResult(T0.AddSeconds(i), Router, router(i)));
            d.Add(new PingResult(T0.AddSeconds(i), Internet, internet(i)));
        }
        return findings;
    }

    [Fact]
    public void Healthy_network_is_quiet() => Assert.Empty(Run(_ => 2, _ => 12));

    [Fact]
    public void Lost_router_pings_blame_the_local_network()
    {
        var f = Assert.Single(Run(i => i is >= 10 and < 14 ? null : 2, i => i is >= 10 and < 14 ? null : 12), x => x.Summary.Contains("router"));
        Assert.Contains("Wi-Fi", f.Summary);
    }

    [Fact]
    public void Internet_only_trouble_blames_the_connection()
    {
        var f = Assert.Single(Run(_ => 2, i => i is >= 10 and < 14 ? 400 : 12));
        Assert.Contains("internet connection", f.Summary);
    }
}
