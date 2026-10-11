using System.Runtime.InteropServices;

namespace StallWatch.Core.Causes;

/// <summary>CPU and memory over the last interval.</summary>
public sealed record CpuSample(
    DateTime Time,
    double CpuPercent,           // all logical CPUs, 0-100
    double BusiestCorePercent,   // the single busiest logical CPU
    double DpcIsrPercent,        // driver time (DPC + interrupts) on the logical CPU where it was highest
    int DpcIsrCore,
    long AvailableMB,
    int MemoryLoadPercent);      // as Task Manager's "in use"

/// <summary>
/// Per-logical-CPU busy and driver (DPC/interrupt) time from SystemProcessorPerformanceInformation, plus memory
/// status. A few hundred bytes per call: cheap enough for every half second, always on, no admin.
/// </summary>
public sealed class CpuSampler
{
    // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION: IdleTime, KernelTime (includes idle), UserTime, DpcTime,
    // InterruptTime (100 ns units), InterruptCount + padding.
    const int EntrySize = 48;
    readonly int _cpus = Environment.ProcessorCount;
    long[]? _last;
    DateTime _lastAt;

    /// <summary>Takes a sample; the first call only primes the counters and returns null.</summary>
    public CpuSample? Sample()
    {
        var now = DateTime.Now;
        var buf = new byte[EntrySize * _cpus];
        if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buf, buf.Length, out _) != 0) return null;
        var cur = new long[_cpus * 4];
        for (var i = 0; i < _cpus; i++)
        {
            var o = i * EntrySize;
            cur[i * 4] = BitConverter.ToInt64(buf, o);                                   // idle
            cur[i * 4 + 1] = BitConverter.ToInt64(buf, o + 8) + BitConverter.ToInt64(buf, o + 16); // kernel + user
            cur[i * 4 + 2] = BitConverter.ToInt64(buf, o + 24) + BitConverter.ToInt64(buf, o + 32); // DPC + interrupt
        }
        var prev = _last;
        var seconds = (now - _lastAt).TotalSeconds;
        _last = cur;
        _lastAt = now;
        if (prev is null || seconds <= 0) return null;

        double busyAll = 0, busiest = 0, dpc = 0;
        var dpcCore = 0;
        for (var i = 0; i < _cpus; i++)
        {
            var total = (double)(cur[i * 4 + 1] - prev[i * 4 + 1]);
            if (total <= 0) continue;
            var busy = 100 * (1 - (cur[i * 4] - prev[i * 4]) / total);
            var drv = 100 * (cur[i * 4 + 2] - prev[i * 4 + 2]) / total;
            busyAll += busy;
            busiest = Math.Max(busiest, busy);
            if (drv > dpc) { dpc = drv; dpcCore = i; }
        }

        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref status);
        return new CpuSample(now, Math.Clamp(busyAll / _cpus, 0, 100), Math.Clamp(busiest, 0, 100), Math.Clamp(dpc, 0, 100), dpcCore,
            (long)(status.ullAvailPhys / (1024 * 1024)), (int)status.dwMemoryLoad);
    }

    const int SystemProcessorPerformanceInformation = 8;

    [DllImport("ntdll.dll")]
    static extern uint NtQuerySystemInformation(int infoClass, byte[] info, int length, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}
