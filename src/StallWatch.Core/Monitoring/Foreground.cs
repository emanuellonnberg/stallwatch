using System.Runtime.InteropServices;

namespace StallWatch.Core.Monitoring;

/// <summary>Which process owns the window the user is looking at.</summary>
public static class Foreground
{
    public static bool Is(int pid) => pid != 0 && Pid() == pid;

    /// <summary>The foreground window's process id, or 0.</summary>
    public static int Pid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var owner);
        return owner;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
}
