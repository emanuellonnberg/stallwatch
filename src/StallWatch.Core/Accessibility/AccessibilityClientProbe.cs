using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StallWatch.Core.Accessibility;

/// <summary>A request for our window's accessibility object (WM_GETOBJECT): someone is reading our UI.</summary>
public record AccessRequest(DateTime Time, int ObjectId)
{
    public const int UiaRootObjectId = -25, OBJID_CLIENT = -4, OBJID_WINDOW = 0;

    public string Api => ObjectId switch
    {
        UiaRootObjectId => "UI Automation",
        OBJID_CLIENT or OBJID_WINDOW => "MSAA",
        _ => $"object {ObjectId}",
    };
}

/// <summary>
/// A small visible window that behaves like a Chromium/Electron app: it raises the accessibility events such apps
/// raise (focus, name change, location change, foreground) and records every WM_GETOBJECT it receives. Programs that
/// answer those events by reading the window's UI are accessibility clients, the kind that switch Chromium apps
/// (VS Code, Edge, Teams, Spotify, Discord) into full accessibility mode, where every UI change sends WinEvents
/// while holding the global window-manager lock. The window cannot see who sent a WM_GETOBJECT; the caller
/// attributes requests with kernel ReadyThread events on <see cref="ThreadId"/> (see AccessRequestAttributor).
/// </summary>
public sealed class AccessibilityClientProbe : IDisposable
{
    readonly Action<AccessRequest> _onRequest;
    readonly Thread _thread;
    readonly ManualResetEventSlim _started = new();
    ProbeForm? _form;

    public int ThreadId { get; private set; }

    public AccessibilityClientProbe(Action<AccessRequest> onRequest)
    {
        _onRequest = onRequest;
        _thread = new Thread(Run) { IsBackground = true, Name = "a11y-probe" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Wait();
    }

    void Run()
    {
        ThreadId = (int)Native.GetCurrentThreadId();
        _form = new ProbeForm(_onRequest);
        _form.Shown += (_, _) => _started.Set();
        Application.Run(_form);
    }

    public void Dispose()
    {
        _form?.BeginInvoke(() => _form.Close());
        _thread.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }

    sealed class ProbeForm : Form
    {
        readonly Action<AccessRequest> _onRequest;
        readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
        int _tick;

        public ProbeForm(Action<AccessRequest> onRequest)
        {
            _onRequest = onRequest;
            Text = "StallWatch accessibility probe";
            Width = 380; Height = 120;
            StartPosition = FormStartPosition.Manual;
            var area = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(area.Right - Width - 20, area.Bottom - Height - 20);
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "StallWatch is checking which programs\nread other windows' UI. Closes by itself.",
            });
            _timer.Tick += (_, _) => RaiseEvents();
            Shown += (_, _) => { Activate(); _timer.Start(); };
        }

        // Look like a Chromium window: some clients only read windows of that class.
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (Native.EnsureClass(ChromiumClass)) cp.ClassName = ChromiumClass;
                return cp;
            }
        }

        // The events a Chromium window raises while the user works in it.
        void RaiseEvents()
        {
            var h = Handle;
            Native.NotifyWinEvent(Native.EVENT_OBJECT_FOCUS, h, AccessRequest.OBJID_CLIENT, 0);
            Native.NotifyWinEvent(Native.EVENT_OBJECT_NAMECHANGE, h, AccessRequest.OBJID_WINDOW, 0);
            Native.NotifyWinEvent(Native.EVENT_OBJECT_LOCATIONCHANGE, h, AccessRequest.OBJID_WINDOW, 0);
            if (++_tick % 8 == 0) Native.NotifyWinEvent(Native.EVENT_SYSTEM_FOREGROUND, h, AccessRequest.OBJID_WINDOW, 0);
        }

        // When we run elevated, UIPI drops WM_GETOBJECT from normal programs; let it through so we see them.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.ChangeWindowMessageFilterEx(Handle, Native.WM_GETOBJECT, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_GETOBJECT) _onRequest(new AccessRequest(DateTime.Now, (int)(long)m.LParam));
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }

    public const string ChromiumClass = "Chrome_WidgetWin_1";

    static class Native
    {
        static bool _registered;

        // Registers a plain window class with this name in our process so WinForms can create (and subclass) it.
        public static bool EnsureClass(string name)
        {
            if (_registered) return true;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0x4000, // CS_GLOBALCLASS: found by WinForms whatever hInstance it looks it up with
                lpfnWndProc = DefWindowProcPtr,
                hInstance = GetModuleHandle(null),
                lpszClassName = name,
            };
            return _registered = RegisterClassEx(ref wc) != 0;
        }

        static readonly IntPtr DefWindowProcPtr = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public uint cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        public const int WM_GETOBJECT = 0x003D;
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_FOCUS = 0x8005,
            EVENT_OBJECT_NAMECHANGE = 0x800C, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

        [DllImport("user32.dll")] public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, uint action, IntPtr changeInfo);
        [DllImport("user32.dll")] public static extern void NotifyWinEvent(uint ev, IntPtr hwnd, int idObject, int idChild);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    }
}
