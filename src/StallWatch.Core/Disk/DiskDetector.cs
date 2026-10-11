using StallWatch.Core.Findings;

namespace StallWatch.Core.Disk;

/// <summary>
/// Turns a stream of completed disk I/Os into per-second disk stats and findings:
/// <list type="bullet">
/// <item>DiskStall: an I/O or flush slower than <see cref="DiskDetectorOptions.StallMs"/>, with who was busy on that disk.</item>
/// <item>FlushStorm: a process forcing writes to disk many times per second for a sustained period.</item>
/// </list>
/// Ops must arrive in roughly increasing time order (as ETW delivers them). Call <see cref="Tick"/> periodically
/// so seconds close even when the disk is idle.
/// </summary>
public sealed class DiskDetector(DiskDetectorOptions options)
{
    public event Action<Finding>? FindingRaised;
    public event Action<DiskSecond>? SecondClosed;

    /// <summary>Optional display name per disk number, e.g. "C: Samsung SSD 980 PRO 2TB".</summary>
    public Func<int, string> DiskName { get; set; } = d => $"disk {d}";

    sealed class ProcStats
    {
        public string Name = "";
        public int Reads, Writes, Flushes;
        public readonly Dictionary<string, int> Files = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class Window
    {
        public int Reads, Writes, Flushes;
        public long Bytes;
        public double MaxMs, MaxFlushMs;
        public DiskOp? Slowest;
        public readonly Dictionary<int, ProcStats> Procs = new();
    }

    sealed class StormState
    {
        public string Name = "";
        public int FlushesThisSecond;
        public int Streak;
        public long StreakFlushes;
        public bool Fired;
        public readonly Dictionary<string, int> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<int> Disks = [];
    }

    // Short memory for correlating other findings (e.g. an app freeze) with what the disks were doing.
    static readonly TimeSpan RecentSpan = TimeSpan.FromSeconds(120);
    const double RecentSlowMs = 50;
    readonly Queue<DiskOp> _recentSlow = new();
    readonly Queue<DiskOp> _recentFlushes = new();

    readonly Dictionary<int, Window> _windows = new();
    readonly Dictionary<int, StormState> _storms = new();
    long _currentSecond = long.MinValue;

    static long SecondOf(DateTime t) => t.Ticks / TimeSpan.TicksPerSecond;

    public void Add(DiskOp op)
    {
        var sec = SecondOf(op.Time);
        if (_currentSecond == long.MinValue) _currentSecond = sec;
        if (sec > _currentSecond) CloseUntil(sec);
        // Late events (sec < current) are folded into the current second; ETW reordering is small.

        Remember(op);

        if (!_windows.TryGetValue(op.Disk, out var w)) _windows[op.Disk] = w = new Window();
        if (!w.Procs.TryGetValue(op.ProcessId, out var p)) w.Procs[op.ProcessId] = p = new ProcStats { Name = op.ProcessName };

        switch (op.Kind)
        {
            case DiskOpKind.Read: w.Reads++; p.Reads++; break;
            case DiskOpKind.Write: w.Writes++; p.Writes++; break;
            case DiskOpKind.Flush:
                w.Flushes++; p.Flushes++;
                if (op.ElapsedMs > w.MaxFlushMs) w.MaxFlushMs = op.ElapsedMs;
                break;
        }
        w.Bytes += op.Bytes;
        if (op.ElapsedMs > w.MaxMs) { w.MaxMs = op.ElapsedMs; w.Slowest = op; }
        if (op.FileName is { Length: > 0 } f && op.Kind != DiskOpKind.Flush)
            p.Files[f] = p.Files.GetValueOrDefault(f) + 1;

        if (!_storms.TryGetValue(op.ProcessId, out var s)) _storms[op.ProcessId] = s = new StormState { Name = op.ProcessName };
        if (op.Kind == DiskOpKind.Flush) { s.FlushesThisSecond++; s.Disks.Add(op.Disk); }
        if (op.Kind == DiskOpKind.Write && op.FileName is { Length: > 0 } wf)
            s.Files[wf] = s.Files.GetValueOrDefault(wf) + 1;
    }

    void Remember(DiskOp op)
    {
        if (op.ElapsedMs >= RecentSlowMs) _recentSlow.Enqueue(op);
        if (op.Kind == DiskOpKind.Flush) _recentFlushes.Enqueue(op);
        var cutoff = op.Time - RecentSpan;
        while (_recentSlow.TryPeek(out var o) && o.Time < cutoff) _recentSlow.Dequeue();
        while (_recentFlushes.TryPeek(out var o) && o.Time < cutoff) _recentFlushes.Dequeue();
    }

    /// <summary>
    /// Plain-language lines about disk trouble overlapping [from, to]: the slowest I/Os in flight and who was
    /// forcing writes. Empty when the disks were fine. Covers roughly the last two minutes.
    /// </summary>
    public List<string> EvidenceFor(DateTime from, DateTime to)
    {
        var lines = new List<string>();
        foreach (var op in _recentSlow
                     .Where(o => o.Time >= from && o.Time - TimeSpan.FromMilliseconds(o.ElapsedMs) <= to)
                     .OrderByDescending(o => o.ElapsedMs).Take(3))
        {
            var what = op.Kind == DiskOpKind.Flush ? "a flush" : op.Kind == DiskOpKind.Read ? "a read" : "a write";
            lines.Add($"{DiskName(op.Disk)}: {what} by {op.ProcessName} took {op.ElapsedMs:F0} ms" +
                      (op.FileName is { } f ? $" ({f})" : ""));
        }
        foreach (var g in _recentFlushes.Where(o => o.Time >= from && o.Time <= to)
                     .GroupBy(o => o.ProcessName).OrderByDescending(g => g.Count()).Take(3))
        {
            if (g.Count() < 5) break;
            var disks = string.Join(", ", g.Select(o => o.Disk).Distinct().Select(DiskName));
            lines.Add($"{g.Key} forced {g.Count()} flushes on {disks} in that time");
        }
        return lines;
    }

    /// <summary>Close every second before <paramref name="now"/>.</summary>
    public void Tick(DateTime now)
    {
        var sec = SecondOf(now);
        if (_currentSecond == long.MinValue) { _currentSecond = sec; return; }
        if (sec > _currentSecond) CloseUntil(sec);
    }

    void CloseUntil(long sec)
    {
        // Close the current second with its data, then any idle seconds in between (bounded so a long gap
        // only needs one idle pass: an idle second breaks every streak anyway).
        CloseSecond(_currentSecond);
        if (sec - _currentSecond > 1) CloseSecond(_currentSecond + 1);
        _currentSecond = sec;
    }

    void CloseSecond(long sec)
    {
        var start = new DateTime(sec * TimeSpan.TicksPerSecond, DateTimeKind.Local);

        foreach (var (disk, w) in _windows)
        {
            SecondClosed?.Invoke(new DiskSecond(start, disk, w.Reads, w.Writes, w.Flushes, w.Bytes, w.MaxMs, w.MaxFlushMs));
            if (w.MaxMs >= options.StallMs && w.Slowest is { } slow)
                FindingRaised?.Invoke(StallFinding(start, disk, w, slow));
        }
        _windows.Clear();

        foreach (var (pid, s) in _storms.ToList())
        {
            if (s.FlushesThisSecond >= options.FlushStormPerSecond)
            {
                s.Streak++;
                s.StreakFlushes += s.FlushesThisSecond;
                if (s.Streak >= options.FlushStormSeconds && !s.Fired)
                {
                    s.Fired = true;
                    FindingRaised?.Invoke(StormFinding(start, s));
                }
            }
            else if (s.FlushesThisSecond == 0 && s.Streak == 0)
            {
                _storms.Remove(pid); // idle process, forget it
                continue;
            }
            else
            {
                s.Streak = 0; s.StreakFlushes = 0; s.Fired = false; s.Files.Clear(); s.Disks.Clear();
            }
            s.FlushesThisSecond = 0;
        }
    }

    Finding StallFinding(DateTime start, int disk, Window w, DiskOp slow)
    {
        var what = slow.Kind switch
        {
            DiskOpKind.Flush => "a forced write (flush)",
            DiskOpKind.Read => "a read",
            _ => "a write",
        };
        var summary = $"{DiskName(disk)} took {slow.ElapsedMs:F0} ms to complete {what} for {slow.ProcessName}. " +
                      "Anything reading or writing that disk at that moment had to wait.";
        var busy = w.Procs.Values
            .OrderByDescending(p => p.Flushes * 10 + p.Writes + p.Reads)
            .Take(4)
            .Select(p => $"{p.Name}: {p.Flushes} flushes, {p.Writes} writes, {p.Reads} reads that second" +
                         (p.Files.Count == 0 ? "" : $" (mostly {p.Files.MaxBy(kv => kv.Value).Key})"))
            .ToList();
        return new Finding(slow.Time, FindingKind.DiskStall, summary, busy, slow.ProcessName);
    }

    Finding StormFinding(DateTime start, StormState s)
    {
        var rate = s.StreakFlushes / (double)s.Streak;
        var disks = string.Join(", ", s.Disks.Select(DiskName));
        var summary = $"{s.Name} is forcing writes to disk ({disks}) about {rate:F0} times per second " +
                      $"for {s.Streak}+ seconds. Each flush makes the drive stop and commit; this is the classic cause " +
                      "of random freezes in other programs using the same drive.";
        var evidence = s.Files.OrderByDescending(kv => kv.Value).Take(3)
            .Select(kv => $"writes to {kv.Key} ({kv.Value} in the storm so far)").ToList();
        return new Finding(start, FindingKind.FlushStorm, summary, evidence, s.Name);
    }
}
