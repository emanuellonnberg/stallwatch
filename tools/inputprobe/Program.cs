// M4 spike: which way of measuring input delay sees a slow global hook, without a focused window?
//  A) Raw Input sink (RIDEV_INPUTSINK): WM_INPUT delivered to a hidden window even when not focused.
//  B) Our own low-level hook, installed BEFORE the slow one (hooks run newest-first, so ours runs after it).
// Each method logs GetTickCount() - event time. Usage: inputprobe <csv> [seconds=60]
using System.Runtime.InteropServices;

var csv = args.Length > 0 ? args[0] : "inputprobe.csv";
var seconds = args.Length > 1 ? int.Parse(args[1]) : 60;
Native.timeBeginPeriod(1);
using var log = new StreamWriter(csv) { AutoFlush = false };
log.WriteLine("time,method,kind,delay_ms");

void Record(string method, string kind, int eventTick)
{
    var d = unchecked((int)Native.GetTickCount() - eventTick);
    if (d is < 0 or > 60000) return;
    log.WriteLine($"{DateTime.Now:HH:mm:ss.fff},{method},{kind},{d}");
}

// B) low-level hooks
Native.HookProc mouseProc = (code, w, l) =>
{
    if (code >= 0) Record("hook", "mouse", Marshal.ReadInt32(l, 16)); // MSLLHOOKSTRUCT.time at offset 16
    return Native.CallNextHookEx(IntPtr.Zero, code, w, l);
};
Native.HookProc keyProc = (code, w, l) =>
{
    if (code >= 0) Record("hook", "key", Marshal.ReadInt32(l, 12)); // KBDLLHOOKSTRUCT.time at offset 12
    return Native.CallNextHookEx(IntPtr.Zero, code, w, l);
};
var mod = Native.GetModuleHandle(null);
var hm = Native.SetWindowsHookEx(14, mouseProc, mod, 0);
var hk = Native.SetWindowsHookEx(13, keyProc, mod, 0);

// A) raw input sink on a message-only window
var sink = new Sink(Record);
var devs = new[]
{
    new Native.RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = 0x100 /* RIDEV_INPUTSINK */, Target = sink.Handle },
    new Native.RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = 0x100, Target = sink.Handle },
};
if (!Native.RegisterRawInputDevices(devs, 2, Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
    Console.WriteLine($"RegisterRawInputDevices failed: {Marshal.GetLastWin32Error()}");

Console.WriteLine($"{DateTime.Now:HH:mm:ss} probing for {seconds} s -> {csv}");
var end = DateTime.Now.AddSeconds(seconds);
while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(1); }
Native.UnhookWindowsHookEx(hm); Native.UnhookWindowsHookEx(hk);
GC.KeepAlive(mouseProc); GC.KeepAlive(keyProc);
log.Flush();
Console.WriteLine("done");

sealed class Sink : NativeWindow
{
    readonly Action<string, string, int> _record;
    public Sink(Action<string, string, int> record)
    {
        _record = record;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x00FF) // WM_INPUT
        {
            uint size = 0;
            var hdr = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
            Native.GetRawInputData(m.LParam, 0x10000005 /* RID_HEADER */, IntPtr.Zero, ref size, hdr);
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(m.LParam, 0x10000005, buf, ref size, hdr) > 0)
                {
                    var type = Marshal.ReadInt32(buf);
                    _record("rawinput", type == 0 ? "mouse" : "key", Native.GetMessageTime());
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        base.WndProc(ref m);
    }
}

static class Native
{
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetTickCount();
    [DllImport("user32.dll")] public static extern int GetMessageTime();
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint p);
    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, int size);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdr);
}
