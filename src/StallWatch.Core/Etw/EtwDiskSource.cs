using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using StallWatch.Core.Disk;

namespace StallWatch.Core.Etw;

/// <summary>
/// Feeds kernel disk I/O completions (with issuing process and file name) into a callback,
/// either live from a real-time kernel session or by replaying an .etl file.
/// </summary>
/// <remarks>
/// Completion events often carry no usable process (flushes in particular), so the issuing process is taken
/// from the matching *Init event (same IRP). File names come from FileKey → name events (create + rundown),
/// because TraceEvent's own resolution misses most of them in real-time sessions.
/// </remarks>
public sealed class EtwDiskSource
{
    /// <summary>Lean flag set: disk completions + issue events + file-name events + process/thread for attribution.
    /// FileIOInit is needed only for FileIoCreate (FileObject → path); it is the costliest flag here.
    /// No FileIO completions and no stacks.</summary>
    public const KernelTraceEventParser.Keywords Keywords =
        KernelTraceEventParser.Keywords.DiskIO |
        KernelTraceEventParser.Keywords.DiskIOInit |
        KernelTraceEventParser.Keywords.DiskFileIO |
        KernelTraceEventParser.Keywords.FileIOInit |
        KernelTraceEventParser.Keywords.Process |
        KernelTraceEventParser.Keywords.Thread |
        KernelTraceEventParser.Keywords.ImageLoad;

    public const string SessionName = "StallWatch-Disk";

    /// <summary>Microsoft-Windows-DxgKrnl "Present" (event 184, keyword Present): one per frame for every graphics
    /// API (D3D9-12, Vulkan, OpenGL), since all of them present through the graphics kernel.</summary>
    public static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    const ulong DxgKrnlPresentKeyword = 0x8000000;
    const int DxgKrnlPresent = 184;

    /// <summary>Microsoft-Windows-DXGI Present_Start (event 42): older recordings only had this; used when a trace
    /// has no DxgKrnl presents.</summary>
    public static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    const int DxgiPresentStart = 42;

    const int MaxNames = 200_000;
    const int MaxPendingIrps = 100_000;

    readonly Action<DiskOp> _onOp;
    readonly Action<int, string, DateTime>? _onPresent;
    readonly Dictionary<ulong, (int Pid, string Name)> _issuer = new();
    readonly Dictionary<ulong, string> _files = new();   // FileKey -> path
    readonly Dictionary<ulong, string> _handles = new(); // FileObject -> path (open handles)

    EtwDiskSource(Action<DiskOp> onOp, Action<int, string, DateTime>? onPresent)
    {
        _onOp = onOp;
        _onPresent = onPresent;
    }

    void Attach(TraceEventDispatcher source)
    {
        var k = source.Kernel;
        k.DiskIOReadInit += RememberIssuer;
        k.DiskIOWriteInit += RememberIssuer;
        k.DiskIOFlushInit += RememberIssuer;
        k.DiskIORead += d => Complete(d, DiskOpKind.Read);
        k.DiskIOWrite += d => Complete(d, DiskOpKind.Write);
        k.DiskIOFlushBuffers += d =>
        {
            var (pid, name) = Issuer(d.Irp, d);
            _onOp(new DiskOp(d.TimeStamp, d.DiskNumber, DiskOpKind.Flush, d.ElapsedTimeMSec, pid, name, null, 0));
        };
        k.FileIOFileCreate += f => SetName(f.FileKey, f.FileName);
        k.FileIOFileRundown += f => SetName(f.FileKey, f.FileName);
        k.FileIOName += f => SetName(f.FileKey, f.FileName);
        k.FileIOFileDelete += f => _files.Remove(f.FileKey);
        // Disk I/O events carry the FileKey (per-file stream context), while FileIoCreate only names a FileObject
        // (per-handle). Cleanup/Close/Flush carry both, which links the two.
        k.FileIOCreate += f =>
        {
            if (string.IsNullOrEmpty(f.FileName)) return;
            if (_handles.Count >= MaxNames) _handles.Clear();
            _handles[f.FileObject] = f.FileName;
        };
        k.FileIOCleanup += f => Link(f.FileObject, f.FileKey);
        k.FileIOClose += f => { Link(f.FileObject, f.FileKey); _handles.Remove(f.FileObject); };
        k.FileIOFlush += f => Link(f.FileObject, f.FileKey);
    }

    void AttachPresents(TraceEventDispatcher source)
    {
        if (_onPresent is null) return;
        var registered = new RegisteredTraceEventParser(source);
        var sawKernelPresent = false;
        registered.All += e =>
        {
            if (e.ProcessID <= 0) return;
            if (e.ProviderGuid == DxgKrnlProvider && (int)e.ID == DxgKrnlPresent)
            {
                sawKernelPresent = true;
                _onPresent(e.ProcessID, Name(e), e.TimeStamp);
            }
            else if (!sawKernelPresent && e.ProviderGuid == DxgiProvider && (int)e.ID == DxgiPresentStart)
                _onPresent(e.ProcessID, Name(e), e.TimeStamp);
        };
    }

    void RememberIssuer(DiskIOInitTraceData d)
    {
        if (d.ProcessID <= 0) return;
        if (_issuer.Count >= MaxPendingIrps) _issuer.Clear(); // completions lost; don't grow forever
        _issuer[d.Irp] = (d.ProcessID, Name(d));
    }

    (int Pid, string Name) Issuer(ulong irp, TraceEvent completion)
    {
        if (_issuer.Remove(irp, out var who)) return who;
        return (completion.ProcessID, Name(completion));
    }

    void Complete(DiskIOTraceData d, DiskOpKind kind)
    {
        var (pid, name) = Issuer(d.Irp, d);
        var file = !string.IsNullOrEmpty(d.FileName) ? d.FileName : _files.GetValueOrDefault(d.FileKey);
        _onOp(new DiskOp(d.TimeStamp, d.DiskNumber, kind, d.ElapsedTimeMSec, pid, name, file, d.TransferSize));
    }

    void Link(ulong fileObject, ulong fileKey)
    {
        if (_handles.TryGetValue(fileObject, out var name)) SetName(fileKey, name);
    }

    void SetName(ulong key, string? name)
    {
        if (key == 0 || string.IsNullOrEmpty(name)) return;
        if (_files.Count >= MaxNames) _files.Clear(); // crude bound; names for active files reappear on next create
        _files[key] = name;
    }

    static string Name(TraceEvent e)
    {
        if (string.IsNullOrEmpty(e.ProcessName)) return e.ProcessID <= 0 ? "unknown" : $"pid {e.ProcessID}";
        return e.ProcessID <= 4 || e.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? e.ProcessName
            : e.ProcessName + ".exe";
    }

    /// <summary>Starts a real-time kernel session. Blocks until <paramref name="ct"/> is cancelled. Needs admin.</summary>
    public static void RunLive(Action<DiskOp> onOp, CancellationToken ct, Action<int, string, DateTime>? onPresent = null)
    {
        using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        session.BufferSizeMB = 64;
        session.EnableKernelProvider(Keywords);
        if (onPresent != null) session.EnableProvider(DxgKrnlProvider, TraceEventLevel.Informational, DxgKrnlPresentKeyword);
        var src = new EtwDiskSource(onOp, onPresent);
        src.Attach(session.Source);
        src.AttachPresents(session.Source);
        using var reg = ct.Register(() => session.Stop());
        session.Source.Process();
    }

    /// <summary>Replays an .etl file through the same handlers.</summary>
    public static void Replay(string etlPath, Action<DiskOp> onOp, Action<int, string, DateTime>? onPresent = null)
    {
        using var source = new ETWTraceEventSource(etlPath);
        var src = new EtwDiskSource(onOp, onPresent);
        src.Attach(source);
        src.AttachPresents(source);
        source.Process();
    }

    /// <summary>Records the same events to an .etl file for <paramref name="duration"/> (expert/offline use).</summary>
    public static void Record(string etlPath, TimeSpan duration)
    {
        using var session = new TraceEventSession(SessionName + "-File", etlPath) { StopOnDispose = true };
        session.BufferSizeMB = 64;
        session.EnableKernelProvider(Keywords);
        session.EnableProvider(DxgKrnlProvider, TraceEventLevel.Informational, DxgKrnlPresentKeyword);
        Thread.Sleep(duration);
    }
}
