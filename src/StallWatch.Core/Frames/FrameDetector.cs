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

    /// <summary>Micro-stutter check (with an app filter, or for the foreground app via <see cref="IsForeground"/>):
    /// window length...</summary>
    public TimeSpan StutterWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>...share of frames that are "late" (over twice the median and at least 16 ms over it)...</summary>
    public double StutterShare { get; init; } = 0.05;

    /// <summary>...and at most one stutter report per app this often.</summary>
    public TimeSpan StutterCooldown { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gaps longer than this are the app being minimized/hidden or idle, not frozen.</summary>
    public double MaxFreezeMs { get; init; } = 10_000;

    /// <summary>Without an app filter, report a freeze only if this says the process is in front of the user (pid →
    /// is foreground). Background apps pause rendering all the time (a browser tab's animation ends) and nobody sees
    /// it. Null = report every app.</summary>
    public Func<int, bool>? IsForeground { get; init; }

    /// <summary>Without an app filter, the foreground app is checked for micro-stutter only if it kept up at least
    /// this frame rate over the whole window: games do, a browser that renders only while scrolling does not.</summary>
    public double StutterMinFpsAnyApp { get; init; } = 30;

    /// <summary>Without an app filter, a freeze counts only after the app rendered without a break (no frame gap
    /// over <see cref="RunGapMs"/>) for this long. Apps that draw in short bursts (a terminal while text scrolls, a
    /// remote-desktop viewer while the remote screen changes) pause all the time without being frozen.</summary>
    public TimeSpan SteadyRunAnyApp { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>A gap longer than this ends a steady run (50 ms = below 20 fps).</summary>
    public double RunGapMs { get; init; } = 50;

    /// <summary>What else happened during a freeze or stutter window: (pid, name, from, to) → causes. Optional.</summary>
    public Func<int, string, DateTime, DateTime, Causes.CauseReport?>? Explain { get; init; }
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
        public DateTime RunStart; // start of the current stretch of rendering without a break
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
            _apps[pid] = new App { Name = name, Last = time, RunStart = time, WindowStart = time };
            return;
        }

        var gapMs = (time - a.Last).TotalMilliseconds;
        if (gapMs < 0) return; // out-of-order delivery; ignore
        var threshold = options.App is null ? options.FreezeMsAnyApp : options.FreezeMs;

        var steadyRun = options.App is not null || a.Last - a.RunStart >= options.SteadyRunAnyApp;
        if (gapMs >= threshold && gapMs <= options.MaxFreezeMs && IsSteady(a) && steadyRun &&
            (options.App is not null || options.IsForeground?.Invoke(pid) != false))
        {
            var (from, fps) = (a.Last, Fps(a));
            Finding Make() => Freeze(pid, a.Name, fps, from, time, gapMs);
            if (options.EvidenceDelay <= TimeSpan.Zero) FindingRaised?.Invoke(Make());
            else _pending.Add((time + options.EvidenceDelay, Make));
        }

        a.Intervals.Enqueue(gapMs);
        if (a.Intervals.Count > IntervalHistory) a.Intervals.Dequeue();
        a.Last = time;
        if (gapMs > options.RunGapMs) a.RunStart = time;

        var watchStutter = options.App is not null || options.IsForeground is not null;
        if (watchStutter && gapMs < threshold) a.Window.Add(gapMs); // freezes are reported separately
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
        if (options.App is null &&
            (a.Window.Count < options.StutterWindow.TotalSeconds * options.StutterMinFpsAnyApp || options.IsForeground?.Invoke(pid) != true))
            return;
        var sorted = a.Window.Order().ToList();
        var median = sorted[sorted.Count / 2];
        if (median > options.SteadyIntervalMs) return;
        var lateLimit = Math.Max(2 * median, median + 16);
        var late = a.Window.Count(x => x > lateLimit);
        var share = late / (double)a.Window.Count;
        if (share < options.StutterShare) return;

        a.LastStutterReport = now;
        var fps = a.Window.Count / a.Window.Sum() * 1000;
        var evidence = new List<string> { $"worst frame {sorted[^1]:F0} ms, 99th percentile {sorted[(int)(sorted.Count * 0.99)]:F0} ms" };
        var causes = options.Explain?.Invoke(pid, a.Name, a.WindowStart, now);
        if (causes?.Likely is { } likely) evidence.Add("likely cause: " + likely);
        if (causes is not null) evidence.AddRange(causes.Evidence);
        FindingRaised?.Invoke(new Finding(now, FindingKind.FrameStutter,
            $"{a.Name} (pid {pid}) is stuttering: {late} of {a.Window.Count} frames ({share:P0}) in the last " +
            $"{options.StutterWindow.TotalSeconds:F0} s took over {lateLimit:F0} ms while a normal frame takes {median:F1} ms " +
            $"(about {fps:F0} fps). You feel this as uneven, jerky motion.",
            evidence, a.Name));
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
        var causes = options.Explain?.Invoke(pid, name, from, to);
        var summary = $"{name} (pid {pid}) froze for {gapMs:F0} ms (no new frame; it was running at about {fps:F0} fps).";
        if (evidence.Count > 0) summary += " The disks were in trouble at the same moment.";
        else if (causes?.Likely is { } likely) summary += $" Likely cause: {likely}.";
        else if (causes is not null) summary += " Disk, CPU, memory, GPU" + (causes.Evidence.Any(e => e.Contains("drivers not traced")) ? "" : " and drivers") +
                                              " looked normal, so it was probably the app itself.";
        else if (disk is not null) summary += " The disks looked fine at that moment, so the cause is elsewhere (CPU, GPU, the app itself).";
        return new Finding(to, FindingKind.FrameFreeze, summary, [.. evidence, .. causes?.Evidence ?? []], name);
    }
}
