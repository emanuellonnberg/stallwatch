using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace StallWatch.Core.Etw;

/// <summary>
/// Reports who wakes one thread. Kernel ReadyThread events carry the awakened thread but not the waker (the header
/// process is -1), so — like xperf — the waker is whatever thread was running on that CPU, tracked from context
/// switches. Dispatcher and context-switch events are system-wide and numerous: short on-demand runs only. Admin.
/// </summary>
public static class EtwReadyThreadSource
{
    public const string SessionName = "StallWatch-Ready";

    /// <summary>All ReadyThread events seen, for any thread (diagnostics).</summary>
    public static long TotalReadyEvents;

    public static void RunLive(int threadId, Action<DateTime, int, string> onReady, CancellationToken ct)
    {
        using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        session.BufferSizeMB = 128;
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.Dispatcher | KernelTraceEventParser.Keywords.ContextSwitch |
                                     KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread);

        var running = new Dictionary<int, (int Pid, string Name)>(); // CPU -> thread currently running there
        var names = new Dictionary<int, string>();
        session.Source.Kernel.ThreadCSwitch += d => running[d.ProcessorNumber] = (d.NewProcessID, d.NewProcessName);
        session.Source.Kernel.DispatcherReadyThread += d =>
        {
            TotalReadyEvents++;
            if (d.AwakenedThreadID != threadId) return;
            if (!running.TryGetValue(d.ProcessorNumber, out var waker) || waker.Pid <= 0) return; // idle/DPC: not a sender
            onReady(d.TimeStamp, waker.Pid, NameOf(waker.Pid, waker.Name));
        };
        using var reg = ct.Register(() => session.Stop());
        session.Source.Process();

        string NameOf(int pid, string? etwName)
        {
            if (!string.IsNullOrEmpty(etwName)) return etwName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? etwName : etwName + ".exe";
            if (names.TryGetValue(pid, out var n)) return n;
            try { n = Process.GetProcessById(pid).ProcessName + ".exe"; } catch (ArgumentException) { n = "pid " + pid; }
            return names[pid] = n;
        }
    }
}
