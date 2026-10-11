using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StallWatch.Core.Input;

/// <summary>
/// Measures how late mouse/keyboard input is delivered, system-wide, without a focused window and without
/// installing a hook of our own: a hidden window registers for Raw Input with RIDEV_INPUTSINK, and each WM_INPUT
/// is timed as GetTickCount() - GetMessageTime(). Raw input is delivered after all global low-level hooks have
/// run, so a slow hook in any program shows up as delay here (validated against tools/slowhook: P95 16 → 78 ms).
/// No admin needed.
/// </summary>
public sealed class RawInputMonitor : IDisposable
{
    readonly Action<DateTime, InputKind, double> _onInput;
    readonly Thread _thread;
    readonly ManualResetEventSlim _started = new();
    uint _threadId;

    public RawInputMonitor(Action<DateTime, InputKind, double> onInput)
    {
        _onInput = onInput;
        _thread = new Thread(Run) { IsBackground = true, Name = "raw-input" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Wait();
    }

    void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        using var sink = new Sink(_onInput);
        var devices = new[]
        {
            new Native.RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = Native.RIDEV_INPUTSINK, Target = sink.Handle }, // mouse
            new Native.RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = Native.RIDEV_INPUTSINK, Target = sink.Handle }, // keyboard
        };
        Native.RegisterRawInputDevices(devices, (uint)devices.Length, Marshal.SizeOf<Native.RAWINPUTDEVICE>());
        _started.Set();
        Application.Run(); // until WM_QUIT
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }

    sealed class Sink : NativeWindow, IDisposable
    {
        readonly Action<DateTime, InputKind, double> _onInput;
        readonly uint _headerSize = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();

        public Sink(Action<DateTime, InputKind, double> onInput)
        {
            _onInput = onInput;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x00FF /* WM_INPUT */)
            {
                var delay = unchecked((int)Native.GetTickCount() - Native.GetMessageTime());
                var header = new Native.RAWINPUTHEADER();
                var size = _headerSize;
                if (delay is >= 0 and < 60_000 &&
                    Native.GetRawInputData(m.LParam, Native.RID_HEADER, ref header, ref size, _headerSize) > 0)
                    _onInput(DateTime.Now, header.Type == 0 ? InputKind.Mouse : InputKind.Keyboard, delay);
            }
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }

    static class Native
    {
        public const uint RIDEV_INPUTSINK = 0x100;
        public const uint RID_HEADER = 0x10000005;

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, int size);

        [DllImport("user32.dll")]
        public static extern uint GetRawInputData(IntPtr handle, uint command, ref RAWINPUTHEADER data, ref uint size, uint headerSize);

        [DllImport("user32.dll")] public static extern int GetMessageTime();
        [DllImport("kernel32.dll")] public static extern uint GetTickCount();
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    }
}
