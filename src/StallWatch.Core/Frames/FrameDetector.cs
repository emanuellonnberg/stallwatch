using StallWatch.Core.Disk;
using StallWatch.Core.Findings;

namespace StallWatch.Core.Frames;

public sealed class FrameDetectorOptions
{
    /// <summary>Only report this process (e.g. "DTS.exe"); null = any app that was presenting steadily.</summary>
    public string? App { get; init; }

    /// <summary>A gap this long between frames is a freeze (when an app filter is set).</summary>
    public double FreezeMs { get; init; } = 150;

    /// <summary>Stricter threshold without an app filter, so ordinary hiccups in random apps stay quiet.</summary>
    public double FreezeMsAnyApp { get; init; } = 250;

    /// <summary>The app must have been presenting at least this often (median interval) to count as "running".</summary>
    public double SteadyIntervalMs { get; init; } = 50;

    /// <summary>Hold a freeze this long before reporting it, so late-arriving disk events (ETW delivers per-CPU
    /// buffers about once a second) can still be attached as evidence. Zero = report immediately.</summary>
    public TimeSpan EvidenceDelay { get; init; } = TimeSpan.Zero;

    /// <summary>Micro-stutter check (only with an app filter): window length...</summary>
    public TimeSpan StutterWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>...share of frames that are "late" (over twice the median and at least 16 ms over it)...</summary>
    public double StutterShare { get; init; } = 0.05;

    /// <summary>...and at most one stutter report per app this often.</summary>
    public TimeSpan StutterCooldown { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gaps longer than this are the app being minimized/hidden or idle, not frozen.</summary>
    public double MaxFreezeMs { get; init; } = 10_000;
}

/// <summary>
/// Detects apps that stop presenting frames for a moment while they were rendering steadily — the visible
/// "stutter". Fed one call per DXGI Present. When a <see cref="DiskDetector"/> is given, each freeze carries
/// what the disks were doing at that moment.
/// </summary>
public sealed class FrameDetector(FrameDetectorOptions options, DiskDetector? disk = null)
{
    public event Action<Finding>? FindingRaised;

    sealed class App
    {
        public required string Name;
        public DateTime Last;
        public readonly Queue<double> Intervals = new();
        public DateTime WindowStart;
        public readonly List<double> Window = [];
        public DateTime LastStutterReport = DateTime.MinValue;
    }

    const int IntervalHistory = 60;
    readonly Dictionary<int, App> _apps = new();
    readonly List<(DateTime Due, Func<Finding> Make)> _pending = [];

    /// <summary>Reports held freezes whose evidence delay has passed.</summary>
    public void Tick(DateTime now)
    {
        foreach (var p in _pending.Where(p => p.Due <= now).ToList())
        {
            _pending.Remove(p);
            FindingRaised?.Invoke(p.Make());
        }
    }

    public void Present(int pid, string name, DateTime time)
    {
        if (options.App is { } only && !string.Equals(only, name, StringComparison.OrdinalIgnoreCase)) return;

        if (!_apps.TryGetValue(pid, out var a) || !string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            _apps[pid] = new App { Name = name, Last = time, WindowStart = time };
            return;
        }

        var gapMs = (time - a.Last).TotalMilliseconds;
        if (gapMs < 0) return; // out-of-order delivery; ignore
        var threshold = options.App is null ? options.FreezeMsAnyApp : options.FreezeMs;

        if (gapMs >= threshold && gapMs <= options.MaxFreezeMs && IsSteady(a))
        {
            var (from, fps) = (a.Last, Fps(a));
            Finding Make() => Freeze(pid, a.Name, fps, from, time, gapMs);
            if (options.EvidenceDelay <= TimeSpan.Zero) FindingRaised?.Invoke(Make());
            else _pending.Add((time + options.EvidenceDelay, Make));
        }

        a.Intervals.Enqueue(gapMs);
        if (a.Intervals.Count > IntervalHistory) a.Intervals.Dequeue();
        a.Last = time;

        if (options.App is not null && gapMs < threshold) a.Window.Add(gapMs); // freezes are reported separately
        if (time - a.WindowStart >= options.StutterWindow)
        {
            CheckStutter(pid, a, time);
            a.Window.Clear();
            a.WindowStart = time;
        }
    }

    void CheckStutter(int pid, App a, DateTime now)
    {
        if (a.Window.Count < 100 || now - a.LastStutterReport < options.StutterCooldown) return;
        var sorted = a.Window.Order().ToList();
        var median = sorted[sorted.Count / 2];
        if (median > options.SteadyIntervalMs) return;
        var lateLimit = Math.Max(2 * median, median + 16);
        var late = a.Window.Count(x => x > lateLimit);
        var share = late / (double)a.Window.Count;
        if (share < options.StutterShare) return;

        a.LastStutterReport = now;
        var fps = a.Window.Count / a.Window.Sum() * 1000;
        FindingRaised?.Invoke(new Finding(now, FindingKind.FrameStutter,
            $"{a.Name} (pid {pid}) is stuttering: {late} of {a.Window.Count} frames ({share:P0}) in the last " +
            $"{options.StutterWindow.TotalSeconds:F0} s took over {lateLimit:F0} ms while a normal frame takes {median:F1} ms " +
            $"(about {fps:F0} fps). You feel this as uneven, jerky motion.",
            [$"worst frame {sorted[^1]:F0} ms, 99th percentile {sorted[(int)(sorted.Count * 0.99)]:F0} ms"]));
    }

    bool IsSteady(App a)
    {
        if (a.Intervals.Count < 20) return false;
        var sorted = a.Intervals.Order().ToList();
        return sorted[sorted.Count / 2] <= options.SteadyIntervalMs;
    }

    // Average rate, not 1/median: apps that present in bursts (two quick frames, then a pause) have a misleadingly
    // small median interval.
    static double Fps(App a) => a.Intervals.Count / a.Intervals.Sum() * 1000;

    Finding Freeze(int pid, string name, double fps, DateTime from, DateTime to, double gapMs)
    {
        var evidence = disk?.EvidenceFor(from, to) ?? [];
        var summary = $"{name} (pid {pid}) froze for {gapMs:F0} ms (no new frame; it was running at about {fps:F0} fps).";
        summary += evidence.Count > 0
            ? " The disks were in trouble at the same moment:"
            : disk is null ? "" : " The disks looked fine at that moment, so the cause is elsewhere (CPU, GPU, the app itself).";
        return new Finding(to, FindingKind.FrameFreeze, summary, evidence);
    }
}
