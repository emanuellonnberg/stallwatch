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
            _apps[pid] = new App { Name = name, Last = time };
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
    }

    bool IsSteady(App a)
    {
        if (a.Intervals.Count < 20) return false;
        var sorted = a.Intervals.Order().ToList();
        return sorted[sorted.Count / 2] <= options.SteadyIntervalMs;
    }

    static double Fps(App a) => 1000 / a.Intervals.Order().ElementAt(a.Intervals.Count / 2);

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
