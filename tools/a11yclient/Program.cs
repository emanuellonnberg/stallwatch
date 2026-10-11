// Positive control for `stallwatch a11y`: a minimal accessibility client, like a screen reader or a UI-reading
// utility. It listens for focus/name-change WinEvents from every program and reads the window's accessibility
// object (AccessibleObjectFromEvent -> WM_GETOBJECT to that window). Usage: a11yclient [seconds=60]
using System.Runtime.InteropServices;
using System.Windows.Forms;

var seconds = args.Length > 0 ? int.Parse(args[0]) : 60;
var reads = 0;
WinEventProc proc = (hook, ev, hwnd, idObject, idChild, thread, time) =>
{
    if (hwnd == IntPtr.Zero) return;
    if (AccessibleObjectFromEvent(hwnd, idObject, idChild, out var acc, out _) == 0 && acc != null)
    {
        reads++;
        Marshal.ReleaseComObject(acc);
    }
};
const uint EVENT_OBJECT_FOCUS = 0x8005, EVENT_OBJECT_NAMECHANGE = 0x800C, WINEVENT_OUTOFCONTEXT = 0;
var hook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT);
Console.WriteLine($"{DateTime.Now:HH:mm:ss} accessibility client active for {seconds} s (pid {Environment.ProcessId})");
var timer = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
timer.Tick += (_, _) => Application.ExitThread();
timer.Start();
Application.Run();
UnhookWinEvent(hook);
GC.KeepAlive(proc);
Console.WriteLine($"{DateTime.Now:HH:mm:ss} done, read {reads} accessibility objects");

delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

static partial class Program
{
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromEvent(IntPtr hwnd, int idObject, int idChild,
        [MarshalAs(UnmanagedType.IDispatch)] out object? acc, out object? child);
}
