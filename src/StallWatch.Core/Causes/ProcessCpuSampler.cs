using System.Runtime.InteropServices;

namespace StallWatch.Core.Causes;

/// <summary>CPU use of one process over a sample interval, in cores (1.0 = one core fully busy).</summary>
public readonly record struct ProcessCpu(int Pid, string Name, double Cores, long HardFaults);

/// <summary>Per-process CPU and paging over the last interval.</summary>
public sealed record ProcessCpuSample(
    DateTime Time,
    TimeSpan Interval,
    double HardFaultsPerSecond,           // page reads from disk, system-wide
    IReadOnlyList<ProcessCpu> Processes); // only those that used any CPU or faulted, busiest first

/// <summary>
/// CPU time and hard page faults of every process from one NtQuerySystemInformation call (no handle per process,
/// no admin). The call returns every thread too (~2 MB, ~12 ms of kernel time here), so sample it every few
/// seconds, not continuously; <see cref="CpuSampler"/> is the cheap per-core view.
/// </summary>
public sealed class ProcessCpuSampler : IDisposable
{
    readonly Dictionary<int, (long Time, uint Faults, long Created)> _last = new();
    DateTime _lastAt;
    nint _buffer;
    int _bufferSize = 1 << 20;

    /// <summary>Takes a sample; the first call only primes the counters and returns null.</summary>
    public ProcessCpuSample? Sample()
    {
        var now = DateTime.Now;
        var procs = Query();
        var first = _lastAt == default;
        var seconds = (now - _lastAt).TotalSeconds;
        var list = new List<ProcessCpu>();
        double faults = 0;
        var seen = new Dictionary<int, (long, uint, long)>(procs.Count);

        foreach (var p in procs)
        {
            var time = p.User + p.Kernel;
            seen[p.Pid] = (time, p.HardFaults, p.Created);
            if (p.Pid == 0) continue;
            if (first || !_last.TryGetValue(p.Pid, out var prev) || prev.Created != p.Created) continue;
            var cores = (time - prev.Time) / 1e7 / seconds;
            var f = (long)(p.HardFaults - prev.Faults);
            faults += f;
            if (cores >= 0.01 || f > 0) list.Add(new ProcessCpu(p.Pid, p.Name, cores, f));
        }

        _last.Clear();
        foreach (var kv in seen) _last[kv.Key] = kv.Value;
        var interval = now - _lastAt;
        _lastAt = now;
        if (first || seconds <= 0) return null;
        list.Sort((a, b) => b.Cores.CompareTo(a.Cores));
        return new ProcessCpuSample(now, interval, faults / seconds, list);
    }

    public void Dispose()
    {
        if (_buffer != 0) Marshal.FreeHGlobal(_buffer);
        _buffer = 0;
    }

    readonly record struct RawProcess(int Pid, string Name, long User, long Kernel, uint HardFaults, long Created);

    // Unmanaged on purpose: the result holds absolute pointers (image names) into the buffer itself.
    List<RawProcess> Query()
    {
        while (true)
        {
            if (_buffer == 0) _buffer = Marshal.AllocHGlobal(_bufferSize);
            var status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferSize, out var needed);
            if (status == STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = 0;
                _bufferSize = Math.Max(needed, _bufferSize) * 2;
                continue;
            }
            if (status != 0) throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{status:X8}");
            break;
        }

        // SYSTEM_PROCESS_INFORMATION (x64): NextEntryOffset@0, HardFaultCount@16, CreateTime@32, UserTime@40,
        // KernelTime@48, ImageName (UNICODE_STRING: Length@56, Buffer@64), UniqueProcessId@80.
        var result = new List<RawProcess>(512);
        var entry = _buffer;
        while (true)
        {
            var next = Marshal.ReadInt32(entry, 0);
            var pid = (int)Marshal.ReadInt64(entry, 80);
            var nameLen = (ushort)Marshal.ReadInt16(entry, 56);
            var namePtr = Marshal.ReadIntPtr(entry, 64);
            var name = pid == 0 ? "Idle" : nameLen == 0 || namePtr == 0 ? "System" : Marshal.PtrToStringUni(namePtr, nameLen / 2);
            result.Add(new RawProcess(pid, name, Marshal.ReadInt64(entry, 40), Marshal.ReadInt64(entry, 48),
                (uint)Marshal.ReadInt32(entry, 16), Marshal.ReadInt64(entry, 32)));
            if (next == 0) break;
            entry += next;
        }
        return result;
    }

    const int SystemProcessInformation = 5;
    const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    [DllImport("ntdll.dll")]
    static extern uint NtQuerySystemInformation(int infoClass, nint info, int length, out int returnLength);


}
