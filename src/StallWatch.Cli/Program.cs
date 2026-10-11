using System.Security.Principal;
using StallWatch.Core.Disk;
using StallWatch.Core.Etw;
using StallWatch.Core.Findings;
using StallWatch.Core.Frames;
using StallWatch.Core.Input;
using StallWatch.Core.Leaks;

namespace StallWatch.Cli;

static class Program
{
    const string Usage = """
        stallwatch - find what makes Windows stutter

          stallwatch watch [--app X.exe] [--minutes N] [--table]
                                                     live: app freezes, disk stalls, flush storms, input delay, leaks (admin)
          stallwatch analyze <file.etl> [--app X.exe] [--table]
                                                     same analysis on a recorded trace
          stallwatch record <file.etl> [--seconds N] record a trace for later analysis (admin)
          stallwatch input [--minutes N] [--log f.csv]
                                                     mouse/keyboard delivery delay only (no admin needed);
                                                     --log writes every event delayed 50 ms or more
          stallwatch leaks [--minutes N]             handle/thread/memory leaks (no admin needed;
                                                     growth needs at least 10 minutes of watching)

          --app     report frame freezes only for this program (e.g. DTS.exe); without it, any app that
                    was rendering steadily and stopped for 250 ms or more is reported
          --table   also print a per-second line for every disk with activity
        """;

    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
        var table = args.Contains("--table");
        var app = StringArg(args, "--app");
        try
        {
            return args[0] switch
            {
                "watch" => Watch(IntArg(args, "--minutes"), table, app),
                "analyze" when args.Length > 1 => Analyze(args[1], table, app),
                "record" when args.Length > 1 => Record(args[1], IntArg(args, "--seconds") ?? 60),
                "leaks" => Leaks(IntArg(args, "--minutes") ?? 0),
                "input" => Input(IntArg(args, "--minutes"), StringArg(args, "--log")),
                _ => Fail(Usage),
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Fail("Access denied starting the kernel trace. Run from an administrator terminal.");
        }
    }

    static int? IntArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : null;
    }

    static string? StringArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static FrameDetector MakeFrameDetector(DiskDetector disk, string? app, bool live)
    {
        var frames = new FrameDetector(new FrameDetectorOptions
        {
            App = app,
            EvidenceDelay = live ? TimeSpan.FromSeconds(3) : TimeSpan.Zero,
        }, disk);
        frames.FindingRaised += PrintFinding;
        return frames;
    }

    static int Fail(string msg) { Console.Error.WriteLine(msg); return 1; }

    static bool IsAdmin() =>
        // Built-in role, not the group name: "Administrators" is localized (e.g. "Administratörer").
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static DiskDetector MakeDetector(bool table)
    {
        var names = DiskInventory.Load();
        var det = new DiskDetector(new DiskDetectorOptions())
        {
            DiskName = d => names.TryGetValue(d, out var n) ? n : $"disk {d}",
        };
        det.FindingRaised += PrintFinding;
        det.SecondClosed += s =>
        {
            var notable = s.MaxMs >= 50 || s.Flushes >= 50;
            if (!table && !notable) return;
            if (s.Reads + s.Writes + s.Flushes == 0) return;
            var line = $"{s.Start:HH:mm:ss} {det.DiskName(s.Disk),-38} {s.Reads,5} rd {s.Writes,5} wr {s.Flushes,5} flush" +
                       $"  max {s.MaxMs,6:F0} ms  {s.Bytes / 1024.0 / 1024.0,7:F1} MB";
            if (notable) WriteColored(line, ConsoleColor.DarkYellow); else Console.WriteLine(line);
        };
        return det;
    }

    static void PrintFinding(Finding f) =>
        WriteColored(f.ToString(), f.Kind switch
        {
            FindingKind.FlushStorm => ConsoleColor.Magenta,
            FindingKind.ResourceLeak => ConsoleColor.Cyan,
            _ => ConsoleColor.Red,
        });

    static LeakDetector MakeLeakDetector()
    {
        var det = new LeakDetector(new LeakDetectorOptions());
        det.FindingRaised += PrintFinding;
        return det;
    }

    // One monitor per process: raw-input registration is per process, a second one would steal the events.
    static (InputDetector det, RawInputMonitor monitor) StartInput(object gate, StreamWriter? log = null, Action<double>? tap = null)
    {
        var det = new InputDetector(new InputDetectorOptions());
        det.FindingRaised += PrintFinding;
        var monitor = new RawInputMonitor((t, kind, ms) =>
        {
            lock (gate)
            {
                det.Add(t, kind, ms);
                tap?.Invoke(ms);
                if (log != null && ms >= 50) log.WriteLine($"{t:HH:mm:ss.fff},{kind},{ms:F0}");
            }
        });
        return (det, monitor);
    }

    static int Input(int? minutes, string? logPath)
    {
        using var log = logPath is null ? null : new StreamWriter(logPath) { AutoFlush = true };
        log?.WriteLine("time,kind,delay_ms");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        if (minutes is { } m) cts.CancelAfter(TimeSpan.FromMinutes(m));
        var gate = new object();
        var window = new List<double>();
        var (_, monitor) = StartInput(gate, log, window.Add);
        using (monitor)
        {
            Console.WriteLine("Watching mouse/keyboard delivery delay. Use the PC normally; Ctrl+C to stop.");
            Console.WriteLine("Every 5 s: events, worst delay, events delayed 100 ms or more.");
            while (!cts.Token.WaitHandle.WaitOne(5000))
            {
                double[] w;
                lock (gate) { w = window.ToArray(); window.Clear(); }
                if (w.Length == 0) continue;
                var late = w.Count(x => x >= 100);
                var line = $"{DateTime.Now:HH:mm:ss}  {w.Length,5} events  worst {w.Max(),6:F0} ms  {late,4} late";
                if (late > 0) WriteColored(line, w.Max() >= 250 ? ConsoleColor.Red : ConsoleColor.DarkYellow);
                else Console.WriteLine(line);
            }
        }
        return 0;
    }

    static readonly TimeSpan LeakPollInterval = TimeSpan.FromSeconds(30);

    static int Leaks(int minutes)
    {
        var det = MakeLeakDetector();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var end = DateTime.Now.AddMinutes(minutes);
        Console.WriteLine(minutes > 0 ? $"Watching for leaks for {minutes} min (snapshot every 30 s). Ctrl+C to stop." : "Checking once.");
        do
        {
            det.Observe(DateTime.Now, ProcessSampler.Snapshot());
        } while (DateTime.Now < end && !cts.Token.WaitHandle.WaitOne(LeakPollInterval));
        Console.WriteLine("Done.");
        return 0;
    }

    static void WriteColored(string text, ConsoleColor color)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }

    static int Watch(int? minutes, bool table, string? app)
    {
        if (!IsAdmin()) return Fail("stallwatch watch needs an administrator terminal (kernel disk tracing).");

        var det = MakeDetector(table);
        var frames = MakeFrameDetector(det, app, live: true);
        var gate = new object();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        if (minutes is { } m) cts.CancelAfter(TimeSpan.FromMinutes(m));

        Console.WriteLine($"Watching disks{(minutes is { } mm ? $" for {mm} min" : "")}. Ctrl+C to stop.");
        var etw = new Thread(() => EtwDiskSource.RunLive(
            op => { lock (gate) det.Add(op); },
            cts.Token,
            (pid, name, t) => { lock (gate) frames.Present(pid, name, t); }))
        { IsBackground = true, Name = "etw" };
        etw.Start();

        var leaks = MakeLeakDetector();
        var (_, inputMonitor) = StartInput(gate);
        using var _input = inputMonitor;
        var nextLeakPoll = DateTime.MinValue;
        while (!cts.Token.WaitHandle.WaitOne(1000))
        {
            lock (gate)
            {
                det.Tick(DateTime.Now.AddSeconds(-1)); // let late ETW events land before closing a second
                frames.Tick(DateTime.Now);
            }
            if (DateTime.Now < nextLeakPoll) continue;
            nextLeakPoll = DateTime.Now + LeakPollInterval;
            var snapshot = ProcessSampler.Snapshot();
            lock (gate) leaks.Observe(DateTime.Now, snapshot);
        }

        etw.Join(TimeSpan.FromSeconds(5));
        lock (gate) det.Tick(DateTime.Now.AddSeconds(2));
        return 0;
    }

    static int Analyze(string etl, bool table, string? app)
    {
        if (!File.Exists(etl)) return Fail($"No such file: {etl}");
        var det = MakeDetector(table);
        var frames = MakeFrameDetector(det, app, live: false);
        DateTime last = default;
        var debug = Environment.GetEnvironmentVariable("STALLWATCH_DEBUG_PROC") is { Length: > 0 } d ? d : null;
        EtwDiskSource.Replay(etl, op =>
        {
            if (debug != null && op.ProcessName.Contains(debug, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"  op {op.Time:HH:mm:ss.fff} {op.Kind} pid={op.ProcessId} {op.ProcessName} disk={op.Disk} file={op.FileName} {op.ElapsedMs:F2}ms");
            det.Add(op);
            last = op.Time;
        }, (pid, name, t) => frames.Present(pid, name, t));
        det.Tick(last.AddSeconds(2));
        return 0;
    }

    static int Record(string etl, int seconds)
    {
        if (!IsAdmin()) return Fail("stallwatch record needs an administrator terminal.");
        Console.WriteLine($"Recording {seconds} s to {etl} ...");
        EtwDiskSource.Record(etl, TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"Done: {new FileInfo(etl).Length / 1024.0 / 1024.0:F1} MB. Analyze with: stallwatch analyze \"{etl}\"");
        return 0;
    }
}
