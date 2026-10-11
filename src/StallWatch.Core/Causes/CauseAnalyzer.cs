using System.Globalization;

namespace StallWatch.Core.Causes;

/// <summary>What else was going on while an app froze: a short verdict (or null) and the lines that support it.</summary>
public sealed record CauseReport(string? Likely, IReadOnlyList<string> Evidence);

/// <summary>
/// Keeps the last couple of minutes of CPU, per-process, GPU, driver and network samples and, for a freeze window,
/// says what stood out. Thresholds are deliberately high: a verdict should be something the user can act on.
/// Thread-safe.
/// </summary>
public sealed class CauseAnalyzer
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(2);

    // Thresholds.
    public double DriverRunMs { get; init; } = 2;         // one DPC/ISR this long is worth a line...
    public double DriverRunLikelyMs { get; init; } = 4;   // ...and this long a verdict (games feel ~4 ms on a 240 Hz frame)
    public double DriverMsPerSecond { get; init; } = 300; // a driver using 30% of a core
    public double CpuFullPercent { get; init; } = 95;
    public double GpuFullPercent { get; init; } = 97;
    public double VramFullShare { get; init; } = 0.95;
    public long RamLowMB { get; init; } = 512;
    public double HardFaultsPerSecond { get; init; } = 500;

    readonly Lock _lock = new();
    readonly List<CpuSample> _cpu = [];
    readonly List<ProcessCpuSample> _procs = [];
    readonly List<GpuSample> _gpu = [];
    readonly List<DriverRun> _runs = [];
    readonly List<(DateTime Second, IReadOnlyDictionary<string, double> Ms)> _driverSeconds = [];
    readonly List<PingResult> _pings = [];

    /// <summary>True while driver (DPC/ISR) tracing runs, so reports can say when drivers were not checked.</summary>
    public bool DriversWatched { get; set; }

    public void Add(CpuSample s) { lock (_lock) Trim(_cpu, s, s.Time); }
    public void Add(ProcessCpuSample s) { lock (_lock) Trim(_procs, s, s.Time); }
    public void Add(GpuSample s) { lock (_lock) Trim(_gpu, s, s.Time); }
    public void Add(DriverRun r) { lock (_lock) Trim(_runs, r, r.Time); }
    public void Add(PingResult p) { lock (_lock) Trim(_pings, p, p.Time); }
    public void AddDriverSecond(DateTime second, IReadOnlyDictionary<string, double> ms) { lock (_lock) Trim(_driverSeconds, (second, ms), second); }

    static void Trim<T>(List<T> list, T item, DateTime now)
    {
        list.Add(item);
        // Items arrive roughly in order; drop from the front.
        var cutoff = now - Keep;
        var drop = 0;
        while (drop < list.Count && TimeOf(list[drop]) < cutoff) drop++;
        if (drop > 0) list.RemoveRange(0, drop);
    }

    static DateTime TimeOf<T>(T item) => item switch
    {
        CpuSample s => s.Time,
        ProcessCpuSample s => s.Time,
        GpuSample s => s.Time,
        DriverRun r => r.Time,
        PingResult p => p.Time,
        ValueTuple<DateTime, IReadOnlyDictionary<string, double>> t => t.Item1,
        _ => DateTime.MaxValue,
    };

    /// <summary>Why might process <paramref name="pid"/> have stopped presenting between from and to?</summary>
    public CauseReport Explain(int pid, string name, DateTime from, DateTime to)
    {
        lock (_lock)
        {
            var found = new List<(double Score, string? Verdict, string Line)>();

            // Samples stamped at the end of their interval overlap the window if they end after it starts and begin
            // before it ends.
            var cpu = _cpu.Where(s => s.Time > from && s.Time <= to + TimeSpan.FromSeconds(0.5)).ToList();
            var procs = _procs.Where(s => s.Time > from && s.Time - s.Interval < to).ToList();
            var gpu = _gpu.Where(s => s.Time > from && s.Time <= to + TimeSpan.FromSeconds(1)).ToList();

            // Drivers.
            var runs = _runs.Where(r => r.Time >= from && r.Time - TimeSpan.FromMilliseconds(r.Ms) <= to).ToList();
            if (runs.Count > 0 && runs.MaxBy(r => r.Ms) is var worst && worst.Ms >= DriverRunMs)
            {
                var what = worst.Interrupt ? "an interrupt" : "a deferred call (DPC)";
                found.Add((worst.Ms >= DriverRunLikelyMs ? 100 + worst.Ms : 20 + worst.Ms,
                    worst.Ms >= DriverRunLikelyMs ? $"the driver {worst.Driver} held CPU {worst.Cpu} for {F(worst.Ms)} ms" : null,
                    $"driver {worst.Driver} ran {what} for {F(worst.Ms)} ms on CPU {worst.Cpu}"));
            }
            foreach (var (second, ms) in _driverSeconds.Where(d => d.Second <= to && d.Second.AddSeconds(1) > from))
                foreach (var (driver, total) in ms.Where(kv => kv.Value >= DriverMsPerSecond))
                    found.Add((40 + total / 10, null, $"driver {driver} used {F(total)} ms of CPU time in that second"));
            if (cpu.Count > 0 && cpu.MaxBy(s => s.DpcIsrPercent) is { DpcIsrPercent: >= 50 } busyDrivers)
                found.Add((45, null, $"CPU {busyDrivers.DpcIsrCore} spent {F(busyDrivers.DpcIsrPercent)}% of its time in drivers"));

            // Memory.
            if (cpu.Count > 0 && cpu.MinBy(s => s.AvailableMB) is { } low && low.AvailableMB < RamLowMB)
                found.Add((90, $"memory was nearly full ({low.AvailableMB} MB free)", $"only {low.AvailableMB} MB of RAM free ({low.MemoryLoadPercent}% in use)"));
            if (procs.Count > 0 && procs.MaxBy(s => s.HardFaultsPerSecond) is { } paging && paging.HardFaultsPerSecond >= HardFaultsPerSecond)
            {
                var own = procs.SelectMany(p => p.Processes).Where(p => p.Pid == pid).Sum(p => p.HardFaults);
                found.Add((85, "Windows was reading memory back from disk (paging)",
                    $"{F(paging.HardFaultsPerSecond)} hard page faults per second" + (own > 0 ? $", {own} of them in {name}" : "")));
            }

            // GPU: the adapter the app renders on, else the busiest one.
            string? appAdapter = gpu.SelectMany(g => g.Processes).Where(p => p.Key == pid).Select(p => p.Value.Adapter).FirstOrDefault();
            var loads = gpu.SelectMany(g => g.Adapters)
                .Where(a => appAdapter is null || a.Name == appAdapter).ToList();
            if (loads.Count > 0)
            {
                var vram = loads.MaxBy(a => a.DedicatedTotalMB > 0 ? a.DedicatedUsedMB / a.DedicatedTotalMB : 0)!;
                if (vram.DedicatedTotalMB > 0 && vram.DedicatedUsedMB >= VramFullShare * vram.DedicatedTotalMB)
                    found.Add((80, $"graphics memory on {vram.Name} was full",
                        $"{vram.Name}: {Gb(vram.DedicatedUsedMB)} of {Gb(vram.DedicatedTotalMB)} GB video memory in use"));
                var busiest = loads.MaxBy(a => a.Busiest3DPercent)!;
                if (busiest.Busiest3DPercent >= GpuFullPercent)
                    found.Add((50, $"the graphics card ({busiest.Name}) was at its limit", $"{busiest.Name} 3D engine at {F(busiest.Busiest3DPercent)}%"));
            }

            // CPU.
            if (cpu.Count > 0 && cpu.MaxBy(s => s.CpuPercent) is { } full && full.CpuPercent >= CpuFullPercent)
            {
                var top = procs.SelectMany(p => p.Processes).Where(p => p.Pid != pid)
                    .GroupBy(p => p.Name).Select(g => (Name: g.Key, Cores: g.Max(p => p.Cores))).OrderByDescending(p => p.Cores).Take(3)
                    .Select(p => $"{p.Name} {F(p.Cores)} cores");
                found.Add((70, "the CPU was fully busy", $"CPU at {F(full.CpuPercent)}%" + (procs.Count > 0 ? "; busiest: " + string.Join(", ", top) : "")));
            }
            // A process that suddenly took much more CPU than it had been (the app itself, or something else).
            var before = _procs.Where(s => s.Time <= from - TimeSpan.FromSeconds(1)).ToList();
            foreach (var spike in procs.SelectMany(p => p.Processes).GroupBy(p => p.Pid).Select(g => g.MaxBy(p => p.Cores)))
            {
                var usual = before.Count == 0 ? 0 : Median(before.Select(s => s.Processes.FirstOrDefault(p => p.Pid == spike.Pid).Cores));
                if (spike.Cores < 2 || spike.Cores < usual * 1.5 + 1) continue;
                var self = spike.Pid == pid;
                found.Add((self ? 60 : 55 + spike.Cores,
                    self ? $"{name} itself was busy (typically loading or compiling shaders)" : $"{spike.Name} suddenly used {F(spike.Cores)} CPU cores",
                    $"{spike.Name} used {F(spike.Cores)} cores (usually {F(usual)})"));
            }

            // Network: not a cause of frozen frames, but worth knowing at the same moment.
            foreach (var target in _pings.Where(p => p.Time >= from - TimeSpan.FromSeconds(1) && p.Time <= to + TimeSpan.FromSeconds(1)).GroupBy(p => p.Target))
            {
                var lost = target.Count(p => p.Ms is null);
                var slow = target.Where(p => p.Ms is not null).Select(p => p.Ms!.Value).DefaultIfEmpty(0).Max();
                if (lost > 0) found.Add((10, null, $"network: {lost} ping(s) to the {target.Key} lost"));
                else if (slow >= 100) found.Add((10, null, $"network: ping to the {target.Key} took {F(slow)} ms"));
            }

            var evidence = found.OrderByDescending(f => f.Score).Select(f => f.Line).Distinct().Take(5).ToList();
            if (Context(pid, cpu, gpu, appAdapter) is { } context) evidence.Add(context);
            var likely = found.Where(f => f.Verdict is not null).OrderByDescending(f => f.Score).Select(f => f.Verdict).FirstOrDefault();
            return new CauseReport(likely, evidence);
        }
    }

    // One line of "what the machine looked like" so a report without a verdict still says something.
    string? Context(int pid, List<CpuSample> cpu, List<GpuSample> gpu, string? appAdapter)
    {
        var parts = new List<string>();
        if (cpu.Count > 0)
        {
            var c = cpu.MaxBy(s => s.CpuPercent)!;
            parts.Add($"CPU {F(c.CpuPercent)}% (busiest core {F(cpu.Max(s => s.BusiestCorePercent))}%)");
            parts.Add($"{Gb(cpu.Min(s => s.AvailableMB))} GB RAM free");
        }
        var adapter = gpu.SelectMany(g => g.Adapters).Where(a => appAdapter is null || a.Name == appAdapter).MaxBy(a => a.Busiest3DPercent);
        if (adapter is not null)
            parts.Add($"{adapter.Name} 3D {F(adapter.Busiest3DPercent)}%, video memory {Gb(adapter.DedicatedUsedMB)}/{Gb(adapter.DedicatedTotalMB)} GB");
        if (!DriversWatched) parts.Add("drivers not traced (only in game mode)");
        return parts.Count == 0 ? null : "At that moment: " + string.Join(", ", parts);
    }

    static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }

    static string F(double v) => v.ToString(v >= 100 ? "F0" : v >= 10 ? "F0" : "0.#", Inv);
    static string Gb(double mb) => (mb / 1024).ToString("F1", Inv);
}
