using System.Diagnostics;
using StallWatch.Core.Findings;
using StallWatch.Core.Monitoring;
using StallWatch.Core.Store;

namespace StallWatch.Tray;

/// <summary>
/// The resident form of StallWatch: the same engine as `stallwatch watch`, a tray icon, rate-limited alerts, and the
/// saved history behind "Open report". Without admin it watches what it can (input delay, leaks) and says so.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    const string SessionName = "StallWatch-Tray";
    static readonly TimeSpan AlertColour = TimeSpan.FromMinutes(2);

    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _status = new() { Enabled = false };
    readonly ToolStripMenuItem _pause = new("Pause");
    readonly ToolStripMenuItem _startWithWindows = new("Start with Windows");
    readonly SynchronizationContext _ui;
    readonly FindingStore? _store;
    readonly AlertThrottle _throttle = new();
    readonly bool _admin = WatchEngine.IsAdmin;
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 30_000 };

    CancellationTokenSource? _cts;
    Task? _run;
    DateTime _since;
    int _count;
    DateTime _lastFinding = DateTime.MinValue;
    string? _engineError;
    FindingsWindow? _window;

    public TrayApp(bool startPaused)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        try { _store = new FindingStore(FindingStore.DefaultPath, flushInterval: TimeSpan.FromSeconds(5)); }
        catch (Exception e) { _engineError = "Not saving findings: " + e.Message; }

        _pause.Click += (_, _) => { if (_run is null) Start(); else Stop(); };
        _startWithWindows.Click += (_, _) => ToggleStartWithWindows();
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _status,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Show findings", null, (_, _) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) },
            new ToolStripMenuItem("Open HTML report", null, (_, _) => OpenReport()),
            _pause,
            _startWithWindows,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);
        menu.Opening += (_, _) => { _startWithWindows.Checked = LogonTask.Exists(); UpdateStatus(); };

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Icon = TrayIcons.Paused, Text = "StallWatch" };
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.BalloonTipClicked += (_, _) => ShowWindow();
        _refresh.Tick += (_, _) => UpdateStatus();
        _refresh.Start();

        if (startPaused) UpdateStatus(); else Start();
    }

    void Start()
    {
        _cts = new CancellationTokenSource();
        _since = DateTime.Now;
        _engineError = null;
        _store?.BeginSession("tray", _since);
        var engine = new WatchEngine();
        engine.FindingRaised += OnFinding;
        var options = new WatchOptions { KernelTrace = _admin, SessionName = SessionName };
        var ct = _cts.Token;
        _run = Task.Run(() => engine.Run(options, ct)).ContinueWith(t =>
        {
            if (t.IsFaulted) _ui.Post(_ => { _engineError = t.Exception!.GetBaseException().Message; Stop(); }, null);
        }, TaskScheduler.Default);
        UpdateStatus();
    }

    void Stop()
    {
        _cts?.Cancel();
        _run?.Wait(TimeSpan.FromSeconds(8));
        _cts?.Dispose();
        _cts = null;
        _run = null;
        _store?.Flush();
        UpdateStatus();
    }

    // Engine thread: keep it short, hand the UI work to the UI thread.
    void OnFinding(Finding f)
    {
        _store?.Add(f);
        Interlocked.Increment(ref _count);
        _ui.Post(_ =>
        {
            _lastFinding = DateTime.Now;
            UpdateStatus();
            if (_window is not null) { _store?.Flush(); _window.Reload(); }
            if (!_throttle.Allow(f)) return;
            _icon.ShowBalloonTip(10_000, $"StallWatch: {HtmlReport.Label(f.Kind)}", Shorten(f.Summary, 250),
                f.Kind is FindingKind.ResourceLeak or FindingKind.AccessibilityClient ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }, null);
    }

    void UpdateStatus()
    {
        string status;
        if (_run is null)
        {
            _icon.Icon = TrayIcons.Paused;
            status = _engineError is null ? "Paused" : "Stopped: " + _engineError;
        }
        else
        {
            var recent = DateTime.Now - _lastFinding < AlertColour;
            _icon.Icon = recent ? TrayIcons.Alert : _admin ? TrayIcons.Watching : TrayIcons.Limited;
            status = (_admin ? "Watching" : "Watching input and leaks only (not admin)") +
                     $" since {_since:HH:mm} · {_count} finding{(_count == 1 ? "" : "s")}";
        }
        _status.Text = status;
        _pause.Text = _run is null ? "Resume" : "Pause";
        _icon.Text = Shorten("StallWatch: " + status, 127); // NotifyIcon.Text limit
    }

    void ShowWindow()
    {
        if (_store is null) return;
        if (_window is null)
        {
            _store.Flush();
            _window = new FindingsWindow(_store);
            _window.FormClosed += (_, _) => { _window.Dispose(); _window = null; };
            _window.Show();
        }
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    void OpenReport()
    {
        if (_store is null) return;
        try
        {
            _store.Flush();
            var path = ReportWriter.Write(_store, days: 7);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _icon.ShowBalloonTip(5000, "StallWatch", "Could not write the report: " + e.Message, ToolTipIcon.Error);
        }
    }

    void ToggleStartWithWindows()
    {
        var on = LogonTask.Exists();
        var ok = on ? LogonTask.Uninstall() : LogonTask.Install(Environment.ProcessPath!);
        _startWithWindows.Checked = LogonTask.Exists();
        if (!ok)
            _icon.ShowBalloonTip(5000, "StallWatch",
                on ? "Could not remove the logon task." : "Could not add the logon task (administrator approval is needed).",
                ToolTipIcon.Warning);
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void ExitThreadCore()
    {
        Stop();
        _window?.Close();
        _refresh.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _store?.Dispose();
        base.ExitThreadCore();
    }
}
