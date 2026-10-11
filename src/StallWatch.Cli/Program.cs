using System.Security.Principal;
using StallWatch.Core.Disk;
using StallWatch.Core.Etw;
using StallWatch.Core.Findings;

namespace StallWatch.Cli;

static class Program
{
    const string Usage = """
        stallwatch - find what makes Windows stutter

          stallwatch watch [--minutes N] [--table]   live: report disk stalls and flush storms (admin)
          stallwatch analyze <file.etl> [--table]    same analysis on a recorded trace
          stallwatch record <file.etl> [--seconds N] record a trace for later analysis (admin)

          --table   also print a per-second line for every disk with activity
        """;

    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
        var table = args.Contains("--table");
        try
        {
            return args[0] switch
            {
                "watch" => Watch(IntArg(args, "--minutes"), table),
                "analyze" when args.Length > 1 => Analyze(args[1], table),
                "record" when args.Length > 1 => Record(args[1], IntArg(args, "--seconds") ?? 60),
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
        WriteColored(f.ToString(), f.Kind == FindingKind.FlushStorm ? ConsoleColor.Magenta : ConsoleColor.Red);

    static void WriteColored(string text, ConsoleColor color)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }

    static int Watch(int? minutes, bool table)
    {
        if (!IsAdmin()) return Fail("stallwatch watch needs an administrator terminal (kernel disk tracing).");

        var det = MakeDetector(table);
        var gate = new object();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        if (minutes is { } m) cts.CancelAfter(TimeSpan.FromMinutes(m));

        Console.WriteLine($"Watching disks{(minutes is { } mm ? $" for {mm} min" : "")}. Ctrl+C to stop.");
        var etw = new Thread(() => EtwDiskSource.RunLive(op => { lock (gate) det.Add(op); }, cts.Token))
        { IsBackground = true, Name = "etw" };
        etw.Start();

        while (!cts.Token.WaitHandle.WaitOne(1000))
            lock (gate) det.Tick(DateTime.Now.AddSeconds(-1)); // let late ETW events land before closing a second

        etw.Join(TimeSpan.FromSeconds(5));
        lock (gate) det.Tick(DateTime.Now.AddSeconds(2));
        return 0;
    }

    static int Analyze(string etl, bool table)
    {
        if (!File.Exists(etl)) return Fail($"No such file: {etl}");
        var det = MakeDetector(table);
        DateTime last = default;
        var debug = Environment.GetEnvironmentVariable("STALLWATCH_DEBUG_PROC") is { Length: > 0 } d ? d : null;
        EtwDiskSource.Replay(etl, op =>
        {
            if (debug != null && op.ProcessName.Contains(debug, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"  op {op.Time:HH:mm:ss.fff} {op.Kind} pid={op.ProcessId} {op.ProcessName} disk={op.Disk} file={op.FileName} {op.ElapsedMs:F2}ms");
            det.Add(op);
            last = op.Time;
        });
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
