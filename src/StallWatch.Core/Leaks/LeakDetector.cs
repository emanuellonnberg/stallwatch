using System.Globalization;
using StallWatch.Core.Findings;

namespace StallWatch.Core.Leaks;

/// <summary>One process at one moment.</summary>
public sealed record ProcessSample(int Pid, string Name, int Handles, int Threads, long PrivateBytes);

public sealed class LeakDetectorOptions
{
    /// <summary>Handle count that is suspicious on its own (normal apps hold hundreds to a few thousand).</summary>
    public int HandleCountAlarm { get; init; } = 50_000;

    /// <summary>Growth must be visible over at least this long before it counts.</summary>
    public TimeSpan MinWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>History kept per process for the trend.</summary>
    public TimeSpan MaxWindow { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Share of sample-to-sample steps that must not go down for growth to count as steady.</summary>
    public double SteadyShare { get; init; } = 0.8;

    public double HandlesPerMinute { get; init; } = 100;
    public int HandlesGrowth { get; init; } = 2000;
    public double ThreadsPerMinute { get; init; } = 5;
    public int ThreadsGrowth { get; init; } = 100;
    public double PrivateMbPerMinute { get; init; } = 50;
    public long PrivateMbGrowth { get; init; } = 2048;
}

/// <summary>
/// Spots processes that leak handles, threads or memory: either an absurd handle count, or steady growth
/// over a sustained window. Each process is reported at most once per kind while it runs.
/// Feed it full process snapshots (e.g. every 30 s) via <see cref="Observe"/>.
/// </summary>
public sealed class LeakDetector(LeakDetectorOptions options)
{
    public event Action<Finding>? FindingRaised;

    sealed class History
    {
        public required string Name;
        public readonly List<(DateTime T, ProcessSample S)> Samples = [];
        public readonly HashSet<string> Reported = [];
    }

    readonly Dictionary<int, History> _procs = new();

    public void Observe(DateTime now, IReadOnlyCollection<ProcessSample> snapshot)
    {
        var alive = new HashSet<int>();
        foreach (var s in snapshot)
        {
            alive.Add(s.Pid);
            if (!_procs.TryGetValue(s.Pid, out var h) || !string.Equals(h.Name, s.Name, StringComparison.OrdinalIgnoreCase))
                _procs[s.Pid] = h = new History { Name = s.Name }; // new process or pid reused

            h.Samples.Add((now, s));
            h.Samples.RemoveAll(x => now - x.T > options.MaxWindow);
            Evaluate(now, s, h);
        }
        foreach (var pid in _procs.Keys.Where(p => !alive.Contains(p)).ToList()) _procs.Remove(pid);
    }

    void Evaluate(DateTime now, ProcessSample s, History h)
    {
        var who = $"{s.Name} (pid {s.Pid})";

        if (s.Handles >= options.HandleCountAlarm && h.Reported.Add("handles-absolute"))
            Raise(now, $"{who} holds {s.Handles.ToString("N0", CultureInfo.InvariantCulture)} handles. Normal programs hold hundreds to a few thousand; " +
                       "this many slows down every tool that lists processes (Task Manager, Resource Monitor) " +
                       "and eats kernel memory. Restarting or disabling that program releases them.");

        if (now - h.Samples[0].T < options.MinWindow) return;

        Trend("handles", x => x.Handles, options.HandlesPerMinute, options.HandlesGrowth, "handles");
        Trend("threads", x => x.Threads, options.ThreadsPerMinute, options.ThreadsGrowth, "threads");
        Trend("memory", x => x.PrivateBytes / 1024.0 / 1024.0, options.PrivateMbPerMinute, options.PrivateMbGrowth, "MB of private memory");

        void Trend(string key, Func<ProcessSample, double> value, double minPerMinute, double minGrowth, string unit)
        {
            if (h.Reported.Contains(key + "-growth")) return;
            var pts = h.Samples.Select(x => ((x.T - h.Samples[0].T).TotalMinutes, value(x.S))).ToList();
            var growth = pts[^1].Item2 - pts[0].Item2;
            if (growth < minGrowth) return;
            var slope = Slope(pts);
            if (slope < minPerMinute) return;
            var steady = pts.Zip(pts.Skip(1), (a, b) => b.Item2 >= a.Item2).Count(up => up) / (double)(pts.Count - 1);
            if (steady < options.SteadyShare) return;

            h.Reported.Add(key + "-growth");
            var minutes = pts[^1].Item1;
            Raise(now, $"{who} keeps growing its {unit}: +{growth:F0} in {minutes:F0} min (about {slope:F0} per minute, " +
                       $"now {pts[^1].Item2:F0}). Steady growth like this is a leak and will slow the system over time.");
        }
    }

    static double Slope(List<(double X, double Y)> pts)
    {
        double n = pts.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (x, y) in pts) { sx += x; sy += y; sxx += x * x; sxy += x * y; }
        var d = n * sxx - sx * sx;
        return d == 0 ? 0 : (n * sxy - sx * sy) / d;
    }

    void Raise(DateTime now, string summary) =>
        FindingRaised?.Invoke(new Finding(now, FindingKind.ResourceLeak, summary, []));
}
