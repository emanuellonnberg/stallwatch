using System.Security.Principal;
using StallWatch.Core.Disk;
using StallWatch.Core.Etw;
using StallWatch.Core.Findings;
using StallWatch.Core.Frames;
using StallWatch.Core.Input;
using StallWatch.Core.Leaks;

namespace StallWatch.Core.Monitoring;

public sealed class WatchOptions
{
    /// <summary>Report frame freezes only for this program; null = any app that was rendering steadily.</summary>
    public string? App { get; init; }

    /// <summary>Disk stalls, flush storms and frame freezes (one kernel ETW session). Needs admin.</summary>
    public bool KernelTrace { get; init; } = true;

    public bool Input { get; init; } = true;
    public bool Leaks { get; init; } = true;

    /// <summary>ETW session name; give each host its own so they do not take over each other's session.</summary>
    public string SessionName { get; init; } = EtwDiskSource.SessionName;

    public TimeSpan LeakPollInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Everything `stallwatch watch` does, for any host (CLI, tray): disk + frame events from one kernel ETW session,
/// input delivery delay from a raw-input sink, and leak polling. Findings come out of <see cref="FindingRaised"/>,
/// raised on engine threads while an internal lock is held — handlers should be quick.
/// </summary>
public sealed class WatchEngine
{
    public event Action<Finding>? FindingRaised;

    /// <summary>Each closed second of disk activity, with the disk's display name.</summary>
    public event Action<DiskSecond, string>? SecondClosed;

    public static bool IsAdmin =>
        // Built-in role, not the group name: "Administrators" is localized (e.g. "Administratörer").
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Watches until <paramref name="ct"/> is cancelled.</summary>
    public void Run(WatchOptions options, CancellationToken ct)
    {
        var gate = new object();
        var names = options.KernelTrace ? DiskInventory.Load() : new Dictionary<int, string>();
        var disk = new DiskDetector(new DiskDetectorOptions()) { DiskName = d => names.TryGetValue(d, out var n) ? n : $"disk {d}" };
        disk.FindingRaised += Raise;
        disk.SecondClosed += s => SecondClosed?.Invoke(s, disk.DiskName(s.Disk));
        var frames = new FrameDetector(new FrameDetectorOptions
        {
            App = options.App,
            EvidenceDelay = TimeSpan.FromSeconds(3),
            IsForeground = Foreground.Is,
        }, disk);
        frames.FindingRaised += Raise;
        var leaks = new LeakDetector(new LeakDetectorOptions());
        leaks.FindingRaised += Raise;
        var input = new InputDetector(new InputDetectorOptions());
        input.FindingRaised += Raise;

        Thread? etw = null;
        if (options.KernelTrace)
        {
            etw = new Thread(() => EtwDiskSource.RunLive(
                op => { lock (gate) disk.Add(op); },
                ct,
                (pid, name, t) => { lock (gate) frames.Present(pid, name, t); },
                options.SessionName))
            { IsBackground = true, Name = "etw" };
            etw.Start();
        }

        using var monitor = options.Input ? new RawInputMonitor((t, kind, ms) => { lock (gate) input.Add(t, kind, ms); }) : null;

        var nextLeakPoll = DateTime.MinValue;
        while (!ct.WaitHandle.WaitOne(1000))
        {
            lock (gate)
            {
                disk.Tick(DateTime.Now.AddSeconds(-1)); // let late ETW events land before closing a second
                frames.Tick(DateTime.Now);
                input.Tick(DateTime.Now);
            }
            if (!options.Leaks || DateTime.Now < nextLeakPoll) continue;
            nextLeakPoll = DateTime.Now + options.LeakPollInterval;
            var snapshot = ProcessSampler.Snapshot();
            lock (gate) leaks.Observe(DateTime.Now, snapshot);
        }

        etw?.Join(TimeSpan.FromSeconds(5));
        lock (gate) disk.Tick(DateTime.Now.AddSeconds(2));
    }

    void Raise(Finding f) => FindingRaised?.Invoke(f);
}
