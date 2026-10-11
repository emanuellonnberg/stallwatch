using System.Runtime.InteropServices;
using System.Security.Principal;
using StallWatch.Core.Causes;
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

    /// <summary>Sample CPU, memory and GPU so freezes can be explained (always on, cheap).</summary>
    public bool Causes { get; init; } = true;

    /// <summary>While a game is in front (foreground app presenting at least <see cref="GameFps"/>), also trace
    /// drivers (DPC/ISR, needs admin) and ping the router and the internet.</summary>
    public bool GameMode { get; init; } = true;

    public double GameFps { get; init; } = 30;

    /// <summary>Game mode ends this long after the game was last in front.</summary>
    public TimeSpan GameModeLinger { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Everything `stallwatch watch` does, for any host (CLI, tray): disk + frame events from one kernel ETW session,
/// input delivery delay from a raw-input sink, leak polling, and cause sampling (CPU, memory, GPU; drivers and network
/// in game mode) that explains freezes. Findings come out of <see cref="FindingRaised"/>, raised on engine threads
/// while an internal lock is held — handlers should be quick.
/// </summary>
public sealed class WatchEngine
{
    public event Action<Finding>? FindingRaised;

    /// <summary>Each closed second of disk activity, with the disk's display name.</summary>
    public event Action<DiskSecond, string>? SecondClosed;

    /// <summary>Game mode started (the game's process name) or ended (null).</summary>
    public event Action<string?>? GameModeChanged;

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
        var causes = options.Causes ? new CauseAnalyzer() : null;
        var frames = new FrameDetector(new FrameDetectorOptions
        {
            App = options.App,
            EvidenceDelay = TimeSpan.FromSeconds(3),
            IsForeground = Foreground.Is,
            Explain = causes is null ? null : causes.Explain,
        }, disk);
        frames.FindingRaised += Raise;
        var leaks = new LeakDetector(new LeakDetectorOptions());
        leaks.FindingRaised += Raise;
        var input = new InputDetector(new InputDetectorOptions());
        input.FindingRaised += Raise;
        var network = new NetworkDetector();
        network.FindingRaised += Raise;
        var presents = new Dictionary<int, (string Name, int Count)>(); // this second, per process

        Thread? etw = null;
        if (options.KernelTrace)
        {
            etw = new Thread(() => EtwDiskSource.RunLive(
                op => { lock (gate) disk.Add(op); },
                ct,
                (pid, name, t) =>
                {
                    lock (gate)
                    {
                        frames.Present(pid, name, t);
                        presents[pid] = (name, presents.GetValueOrDefault(pid).Count + 1);
                    }
                },
                options.SessionName))
            { IsBackground = true, Name = "etw" };
            etw.Start();
        }

        using var monitor = options.Input ? new RawInputMonitor((t, kind, ms) => { lock (gate) input.Add(t, kind, ms); }) : null;

        var cpu = causes is null ? null : new CpuSampler();
        using var procs = causes is null ? null : new ProcessCpuSampler();
        using var gpu = causes is null ? null : TryCreate(() => new GpuSampler());
        var game = new GameMode(options, causes, gate, network, name => GameModeChanged?.Invoke(name));

        var nextLeakPoll = DateTime.MinValue;
        var nextProcs = DateTime.MinValue;
        var nextSecond = DateTime.MinValue;
        try
        {
            while (!ct.WaitHandle.WaitOne(500))
            {
                var now = DateTime.Now;
                if (cpu?.Sample() is { } c) causes!.Add(c);
                if (now >= nextProcs) { nextProcs = now.AddSeconds(2); if (procs?.Sample() is { } p) causes!.Add(p); }
                if (now < nextSecond) continue;
                nextSecond = now.AddSeconds(1);

                if (gpu?.Sample() is { } g) causes!.Add(g);
                Dictionary<int, (string Name, int Count)> lastSecond;
                lock (gate)
                {
                    disk.Tick(now.AddSeconds(-1)); // let late ETW events land before closing a second
                    frames.Tick(now);
                    input.Tick(now);
                    lastSecond = new(presents);
                    presents.Clear();
                }
                game.Update(now, lastSecond);

                if (!options.Leaks || now < nextLeakPoll) continue;
                nextLeakPoll = now + options.LeakPollInterval;
                var snapshot = ProcessSampler.Snapshot();
                lock (gate) leaks.Observe(now, snapshot);
            }
        }
        finally { game.Stop(); }

        etw?.Join(TimeSpan.FromSeconds(5));
        lock (gate) disk.Tick(DateTime.Now.AddSeconds(2));
    }

    static T? TryCreate<T>(Func<T> make) where T : class
    {
        try { return make(); }
        catch (Exception e) when (e is InvalidOperationException or COMException or DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>Starts driver tracing and pings while a game is in front, and stops them afterwards.</summary>
    sealed class GameMode(WatchOptions options, CauseAnalyzer? causes, object gate, NetworkDetector network, Action<string?> changed)
    {
        CancellationTokenSource? _cts;
        Task? _drivers, _pings;
        DateTime _until;

        public void Update(DateTime now, Dictionary<int, (string Name, int Count)> presentsLastSecond)
        {
            if (!options.GameMode || causes is null) return;
            var fg = Foreground.Pid();
            if (fg != 0 && presentsLastSecond.TryGetValue(fg, out var p) && p.Count >= options.GameFps)
            {
                _until = now + options.GameModeLinger;
                if (_cts is null) Start(p.Name);
            }
            else if (_cts is not null && now > _until) Stop();
        }

        void Start(string name)
        {
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            if (IsAdmin)
            {
                causes!.DriversWatched = true;
                _drivers = Task.Run(() => DriverMonitor.RunLive(options.SessionName + "-Drivers", causes.Add, causes.AddDriverSecond, ct), ct);
            }
            _pings = Task.Run(() => NetworkMonitor.RunAsync(r => { causes!.Add(r); lock (gate) network.Add(r); }, ct), ct);
            changed(name);
        }

        public void Stop()
        {
            if (_cts is null) return;
            _cts.Cancel();
            try { Task.WaitAll([.. new[] { _drivers, _pings }.OfType<Task>()], TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { } // cancelled, or tracing failed: nothing to keep
            _cts.Dispose();
            _cts = null;
            causes!.DriversWatched = false;
            changed(null);
        }
    }

    void Raise(Finding f) => FindingRaised?.Invoke(f);
}
