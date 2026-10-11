// Times NotifyWinEvent: the call every UI app makes when something on screen changes.
// Its cost grows with the number of WinEvent listeners (SetWinEventHook) system-wide, and it holds
// the global window-manager lock while it runs, so a slow call here stalls input for everyone.
using System.Diagnostics;
using System.Runtime.InteropServices;

var count = args.Length > 0 ? int.Parse(args[0]) : 2000;
using var form = new Form { ShowInTaskbar = false, Opacity = 0 };
var hwnd = form.Handle;

const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
const int OBJID_WINDOW = 0, CHILDID_SELF = 0;

form.Show();
Application.DoEvents();
foreach (var (name, ev) in new[] { ("LOCATIONCHANGE", EVENT_OBJECT_LOCATIONCHANGE), ("REORDER", 0x8004u), ("VALUECHANGE", 0x800Eu),
                                   ("NAMECHANGE", 0x800Cu), ("FOCUS", 0x8005u), ("SYSTEM_MOVESIZESTART", 0x000Au) })
{
    for (var i = 0; i < 50; i++) NotifyWinEvent(ev, hwnd, OBJID_WINDOW, CHILDID_SELF);
    var times = new double[count];
    var sw = new Stopwatch();
    for (var i = 0; i < count; i++)
    {
        sw.Restart();
        NotifyWinEvent(ev, hwnd, OBJID_WINDOW, CHILDID_SELF);
        times[i] = sw.Elapsed.TotalMicroseconds;
    }
    Array.Sort(times);
    Console.WriteLine($"{name,-22} x{count}: median {times[count / 2],6:F0} us, p95 {times[count * 95 / 100],6:F0} us, max {times[^1],7:F0} us");
    Application.DoEvents();
}

[DllImport("user32.dll")] static extern void NotifyWinEvent(uint ev, IntPtr hwnd, int idObject, int idChild);
