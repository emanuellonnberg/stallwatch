using System.Diagnostics;

namespace StallWatch.Core.Leaks;

/// <summary>Takes a cheap snapshot of every process's handle, thread and private-memory counts. No admin needed
/// for most processes; ones we cannot read are skipped.</summary>
public static class ProcessSampler
{
    public static List<ProcessSample> Snapshot()
    {
        var list = new List<ProcessSample>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id is 0 or 4) continue; // Idle / System: kernel-owned counts, not a leak signal
                    list.Add(new ProcessSample(p.Id, p.ProcessName + ".exe", p.HandleCount, p.Threads.Count, p.PrivateMemorySize64));
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // exited or protected; skip
                }
            }
        }
        return list;
    }
}
