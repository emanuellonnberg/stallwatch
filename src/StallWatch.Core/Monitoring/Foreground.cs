using System.Runtime.InteropServices;

namespace StallWatch.Core.Monitoring;

/// <summary>Which process owns the window the user is looking at.</summary>
public static class Foreground
{
    public static bool Is(int pid)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var owner);
        return owner == pid;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
}
