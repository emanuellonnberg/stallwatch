// Positive control for StallWatch input-delay detection: a global low-level mouse+keyboard hook that answers
// slowly, the way a misbehaving app (e.g. ChatGPT Classic) can. Every Nth input event is held for delayMs.
// Usage: slowhook [delayMs=60] [everyNth=10] [seconds=30]. Exits by itself; Windows also bypasses hooks that
// exceed its LowLevelHooksTimeout, so input can never be lost.
using System.Runtime.InteropServices;

var delayMs = args.Length > 0 ? int.Parse(args[0]) : 60;
var everyNth = args.Length > 1 ? int.Parse(args[1]) : 10;
var seconds = args.Length > 2 ? int.Parse(args[2]) : 30;

long n = 0;
Native.HookProc proc = (code, wParam, lParam) =>
{
    if (code >= 0 && Interlocked.Increment(ref n) % everyNth == 0) Thread.Sleep(delayMs);
    return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
};
var module = Native.GetModuleHandle(null);
var mouse = Native.SetWindowsHookEx(14 /* WH_MOUSE_LL */, proc, module, 0);
var keys = Native.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, module, 0);
Console.WriteLine($"{DateTime.Now:HH:mm:ss} slow hook active: every {everyNth}th input event delayed {delayMs} ms, for {seconds} s");

var end = DateTime.Now.AddSeconds(seconds);
while (DateTime.Now < end) Application.DoEvents(); // low-level hooks need a message loop on this thread
Native.UnhookWindowsHookEx(mouse);
Native.UnhookWindowsHookEx(keys);
GC.KeepAlive(proc);
Console.WriteLine($"{DateTime.Now:HH:mm:ss} slow hook removed after {n} events");

static class Native
{
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
}
