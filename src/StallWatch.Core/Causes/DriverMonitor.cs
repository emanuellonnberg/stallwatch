using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace StallWatch.Core.Causes;

/// <summary>A driver routine that held a CPU (DPC or interrupt service routine).</summary>
public readonly record struct DriverRun(DateTime Time, string Driver, double Ms, bool Interrupt, int Cpu);

/// <summary>
/// Which drivers hold CPUs, from kernel DPC and ISR events. The routine address is mapped to the driver whose image
/// contains it (kernel image loads, with the rundown of already-loaded drivers at session start). DPCs run tens of
/// thousands of times a second, so this is a separate session that hosts start only while it matters (a game in the
/// foreground) and stop again. Reports runs of at least <see cref="MinMs"/>; shorter ones are summed per second.
/// Needs admin.
/// </summary>
public static class DriverMonitor
{
    public const double MinMs = 0.5;

    /// <param name="onRun">Each DPC/ISR of at least MinMs.</param>
    /// <param name="onSecond">Each second: total DPC+ISR milliseconds per driver.</param>
    public static void RunLive(string sessionName, Action<DriverRun> onRun, Action<DateTime, IReadOnlyDictionary<string, double>> onSecond,
                               CancellationToken ct)
    {
        using var session = new TraceEventSession(sessionName) { StopOnDispose = true };
        session.BufferSizeMB = 64;
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.DeferedProcedureCalls |
                                     KernelTraceEventParser.Keywords.Interrupt |
                                     KernelTraceEventParser.Keywords.ImageLoad);
        var drivers = new DriverMap();
        var second = DateTime.MinValue;
        var totals = new Dictionary<string, double>();

        void Image(ImageLoadTraceData d)
        {
            if (d.ProcessID is 0 or 4 || d.ImageBase >= 0xFFFF_8000_0000_0000) drivers.Add(d.ImageBase, (ulong)d.ImageSize, d.FileName);
        }
        void Run(DateTime time, ulong routine, double ms, bool isr, int cpu)
        {
            var name = drivers.Find(routine);
            var s = time.AddTicks(-(time.Ticks % TimeSpan.TicksPerSecond));
            if (s != second)
            {
                if (second != DateTime.MinValue && totals.Count > 0) onSecond(second, new Dictionary<string, double>(totals));
                totals.Clear();
                second = s;
            }
            totals[name] = totals.GetValueOrDefault(name) + ms;
            if (ms >= MinMs) onRun(new DriverRun(time, name, ms, isr, cpu));
        }

        session.Source.Kernel.ImageDCStart += Image;
        session.Source.Kernel.ImageLoad += Image;
        session.Source.Kernel.PerfInfoDPC += d => Run(d.TimeStamp, d.Routine, d.ElapsedTimeMSec, false, d.ProcessorNumber);
        session.Source.Kernel.PerfInfoTimerDPC += d => Run(d.TimeStamp, d.Routine, d.ElapsedTimeMSec, false, d.ProcessorNumber);
        session.Source.Kernel.PerfInfoThreadedDPC += d => Run(d.TimeStamp, d.Routine, d.ElapsedTimeMSec, false, d.ProcessorNumber);
        session.Source.Kernel.PerfInfoISR += d => Run(d.TimeStamp, d.Routine, d.ElapsedTimeMSec, true, d.ProcessorNumber);
        using var reg = ct.Register(() => session.Stop());
        session.Source.Process();
    }

    /// <summary>Kernel address ranges of loaded drivers.</summary>
    sealed class DriverMap
    {
        readonly List<(ulong Start, ulong End, string Name)> _ranges = [];
        bool _sorted = true;

        public void Add(ulong start, ulong size, string path)
        {
            if (size == 0) return;
            _ranges.Add((start, start + size, Path.GetFileName(path) is { Length: > 0 } n ? n : path));
            _sorted = false;
        }

        public string Find(ulong address)
        {
            if (!_sorted) { _ranges.Sort((a, b) => a.Start.CompareTo(b.Start)); _sorted = true; }
            int lo = 0, hi = _ranges.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var r = _ranges[mid];
                if (address < r.Start) hi = mid - 1;
                else if (address >= r.End) lo = mid + 1;
                else return r.Name;
            }
            return "unknown driver";
        }
    }
}
