using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace StallWatch.Core.Causes;

/// <summary>One graphics adapter over the last interval.</summary>
public sealed record AdapterLoad(string Name, double Busiest3DPercent, double BusiestAnyPercent, string BusiestEngine,
                                 double DedicatedUsedMB, double DedicatedTotalMB);

/// <summary>A process's load on the 3D engine it uses most.</summary>
public readonly record struct ProcessGpu(string Adapter, double Percent3D);

public sealed record GpuSample(DateTime Time, IReadOnlyList<AdapterLoad> Adapters, IReadOnlyDictionary<int, ProcessGpu> Processes);

/// <summary>
/// GPU engine load per adapter and per process, and dedicated video memory in use, from the "GPU Engine" and
/// "GPU Adapter Memory" performance counters (what Task Manager shows). English counter names through
/// PdhAddEnglishCounter, so it works on any Windows display language. Adapter names and VRAM sizes come from DXGI.
/// </summary>
public sealed partial class GpuSampler : IDisposable
{
    readonly nint _query;
    readonly nint _engine, _memory;
    readonly Dictionary<string, (string Name, double TotalMB)> _adapters; // key: "0x{high:X8}_0x{low:X8}"
    bool _primed;

    public GpuSampler()
    {
        _adapters = DxgiAdapters();
        Check(PdhOpenQueryW(null, 0, out _query), "PdhOpenQuery");
        Check(PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _engine), "GPU Engine counter");
        Check(PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out _memory), "GPU Adapter Memory counter");
    }

    /// <summary>Takes a sample; the first call only primes the rate counters and returns null.</summary>
    public GpuSample? Sample()
    {
        if (PdhCollectQueryData(_query) != 0) return null;
        if (!_primed) { _primed = true; return null; }

        // Engine instances: pid_<pid>_luid_0x<hi>_0x<lo>_phys_<n>_eng_<n>_engtype_<type>. Several processes share an
        // engine, so an engine's load is the sum over its processes.
        var engines = new Dictionary<(string Luid, string Eng, string Type), double>();
        var per3D = new Dictionary<int, (string Luid, double Percent)>();
        foreach (var (name, value) in Read(_engine))
        {
            var m = EngineName().Match(name);
            if (!m.Success) continue;
            var key = (m.Groups["luid"].Value, m.Groups["eng"].Value, m.Groups["type"].Value);
            engines[key] = engines.GetValueOrDefault(key) + value;
            if (key.Item3 == "3D" && int.TryParse(m.Groups["pid"].Value, out var pid) && value > per3D.GetValueOrDefault(pid).Percent)
                per3D[pid] = (key.Item1, value);
        }
        var memory = new Dictionary<string, double>();
        foreach (var (name, value) in Read(_memory))
            if (MemoryName().Match(name) is { Success: true } m)
                memory[m.Groups["luid"].Value] = memory.GetValueOrDefault(m.Groups["luid"].Value) + value / (1024 * 1024);

        var adapters = new List<AdapterLoad>();
        foreach (var luid in engines.Keys.Select(k => k.Luid).Concat(memory.Keys).Distinct())
        {
            if (!_adapters.TryGetValue(luid, out var info)) continue; // software/virtual adapters
            var mine = engines.Where(e => e.Key.Luid == luid).ToList();
            var busiest = mine.Count == 0 ? default : mine.MaxBy(e => e.Value);
            adapters.Add(new AdapterLoad(info.Name,
                Math.Min(100, mine.Where(e => e.Key.Type == "3D").Select(e => e.Value).DefaultIfEmpty(0).Max()),
                Math.Min(100, busiest.Value), busiest.Key.Type ?? "",
                memory.GetValueOrDefault(luid), info.TotalMB));
        }
        var processes = per3D.Where(p => _adapters.ContainsKey(p.Value.Luid))
            .ToDictionary(p => p.Key, p => new ProcessGpu(_adapters[p.Value.Luid].Name, p.Value.Percent));
        return new GpuSample(DateTime.Now, adapters, processes);
    }

    [GeneratedRegex(@"pid_(?<pid>\d+)_luid_(?<luid>0x[0-9A-Fa-f]{8}_0x[0-9A-Fa-f]{8})_phys_\d+_eng_(?<eng>\d+)_engtype_(?<type>.+)$")]
    private static partial Regex EngineName();

    [GeneratedRegex(@"luid_(?<luid>0x[0-9A-Fa-f]{8}_0x[0-9A-Fa-f]{8})_phys_\d+")]
    private static partial Regex MemoryName();

    List<(string Name, double Value)> Read(nint counter)
    {
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, 0);
        if (status != PDH_MORE_DATA || size == 0) return [];
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buffer) != 0) return [];
            var list = new List<(string, double)>((int)count);
            var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (item.CStatus is 0 or 1) list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.DoubleValue)); // VALID / NEW_DATA
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static Dictionary<string, (string, double)> DxgiAdapters()
    {
        var result = new Dictionary<string, (string, double)>();
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factory) != 0) return result;
        try
        {
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
            {
                try
                {
                    if (adapter.GetDesc1(out var d) != 0 || (d.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                    var key = string.Create(CultureInfo.InvariantCulture, $"0x{d.LuidHigh:X8}_0x{d.LuidLow:X8}");
                    result[key] = (d.Description.TrimEnd('\0'), d.DedicatedVideoMemory / (1024.0 * 1024));
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
        }
        finally { Marshal.ReleaseComObject(factory); }
        return result;
    }

    public void Dispose() => PdhCloseQuery(_query);

    static void Check(uint status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"{what} failed: 0x{status:X8}");
    }

    const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000, PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public nint Name;
        public uint CStatus;
        public double DoubleValue; // union; 8-byte aligned after CStatus
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? source, nint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(nint query);

    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    // Only the vtable slots up to the ones we call matter; the placeholders keep the order.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
