using System.Diagnostics;
using System.Globalization;
using StallWatch.Core.Findings;
using StallWatch.Core.Store;

namespace StallWatch.Tray;

/// <summary>
/// The saved findings in a window: a folded timeline grouped by day (same kind and program on one day = one row),
/// the details of the selected row, and the programs behind most findings. Reload() refreshes it while open.
/// </summary>
sealed class FindingsWindow : Form
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly (string Label, int Days)[] Ranges = [("Today", 1), ("Last 7 days", 7), ("Last 30 days", 30), ("Last 365 days", 365)];

    readonly FindingStore _store;
    readonly Label _summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 8, 16, 4) };
    readonly ComboBox _range = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    readonly ListView _timeline = NewList();
    readonly ListView _programs = NewList();
    readonly TextBox _details = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10f), BackColor = SystemColors.Window,
    };
    readonly ImageList _dots = new() { ImageSize = new Size(12, 12), ColorDepth = ColorDepth.Depth32Bit };

    public FindingsWindow(FindingStore store)
    {
        _store = store;
        Text = "StallWatch findings";
        Icon = TrayIcons.Watching;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1100, 720);
        MinimumSize = new Size(640, 400);
        Font = new Font("Segoe UI", 9.5f);

        foreach (var k in Enum.GetValues<FindingKind>()) _dots.Images.Add(k.ToString(), Dot(KindColor(k)));
        _timeline.SmallImageList = _dots;
        _timeline.Columns.Add("When", 120);
        _timeline.Columns.Add("Type", 110);
        _timeline.Columns.Add("×", 40, HorizontalAlignment.Right);
        _timeline.Columns.Add("Program", 170);
        _timeline.Columns.Add("What happened", 620);
        _timeline.SelectedIndexChanged += (_, _) => ShowDetails();
        _programs.Columns.Add("Program", 240);
        _programs.Columns.Add("Findings", 80, HorizontalAlignment.Right);
        _programs.Columns.Add("What", 460);
        _programs.Columns.Add("Last seen", 130);

        _range.Items.AddRange(Ranges.Select(r => (object)r.Label).ToArray());
        _range.SelectedIndex = 1;
        _kind.Items.Add("All types");
        _kind.Items.AddRange(Enum.GetValues<FindingKind>().Select(k => (object)HtmlReport.Label(k)).ToArray());
        _kind.SelectedIndex = 0;
        _range.SelectedIndexChanged += (_, _) => Reload();
        _kind.SelectedIndexChanged += (_, _) => Reload();

        var export = new Button { Text = "Export HTML…", AutoSize = true };
        export.Click += (_, _) => ExportHtml();
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 6), WrapContents = false };
        bar.Controls.AddRange([_summary, _range, _kind, export]);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_timeline);
        split.Panel2.Controls.Add(_details);
        split.Panel2.Padding = new Padding(10, 6, 10, 6);
        split.Panel2.BackColor = SystemColors.Window;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var timelineTab = new TabPage("Timeline");
        timelineTab.Controls.Add(split);
        var programsTab = new TabPage("Programs");
        programsTab.Controls.Add(_programs);
        tabs.TabPages.AddRange([timelineTab, programsTab]);

        Controls.Add(tabs);
        Controls.Add(bar);
        Load += (_, _) => { split.SplitterDistance = (int)(split.Height * 0.62); Reload(); };
        // Last column takes the remaining width.
        _timeline.Resize += (_, _) => FillLastColumn(_timeline);
        _programs.Resize += (_, _) => FillLastColumn(_programs);
    }

    static void FillLastColumn(ListView list)
    {
        if (list.Columns.Count == 0) return;
        var used = list.Columns.Cast<ColumnHeader>().Take(list.Columns.Count - 1).Sum(c => c.Width);
        list.Columns[^1].Width = Math.Max(120, list.ClientSize.Width - used - SystemInformation.VerticalScrollBarWidth);
    }

    static ListView NewList() => new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
        BorderStyle = BorderStyle.None, MultiSelect = false,
    };

    int Days => Ranges[Math.Max(0, _range.SelectedIndex)].Days;
    FindingKind? KindFilter => _kind.SelectedIndex <= 0 ? null : Enum.GetValues<FindingKind>()[_kind.SelectedIndex - 1];

    /// <summary>Reads the store again, keeping the selected row if it is still there.</summary>
    public void Reload()
    {
        var to = DateTime.Today.AddDays(1);
        var from = to.AddDays(-Days);
        var all = _store.Findings(from, to);
        var watched = HtmlReport.Watched(_store.Sessions(from, to), from, to);
        var findings = KindFilter is { } k ? all.Where(f => f.Kind == k).ToList() : all;

        _summary.Text = $"{Hours(watched)} watched · {findings.Count:N0} finding{(findings.Count == 1 ? "" : "s")}";
        var selectedKey = _timeline.SelectedItems.Count > 0 ? (string?)_timeline.SelectedItems[0].Name : null;

        _timeline.BeginUpdate();
        _timeline.Items.Clear();
        _timeline.Groups.Clear();
        var groups = new Dictionary<DateTime, ListViewGroup>();
        foreach (var g in HtmlReport.Fold(findings))
        {
            var f = g[0];
            var day = f.Time.Date;
            if (!groups.TryGetValue(day, out var lvGroup))
            {
                lvGroup = new ListViewGroup(DayTitle(day));
                groups[day] = lvGroup;
                _timeline.Groups.Add(lvGroup);
            }
            var when = g.Count == 1 ? f.Time.ToString("HH:mm:ss", Inv) : $"{g[^1].Time.ToString("HH:mm", Inv)}–{f.Time.ToString("HH:mm", Inv)}";
            var item = new ListViewItem([when, HtmlReport.Label(f.Kind), g.Count == 1 ? "" : g.Count.ToString(Inv), f.Process ?? "", f.Summary], f.Kind.ToString())
            {
                Group = lvGroup, Tag = g, Name = $"{day:yyyyMMdd}|{f.Kind}|{f.Process}",
            };
            _timeline.Items.Add(item);
        }
        if (findings.Count == 0)
            _timeline.Items.Add(new ListViewItem(["", "", "", "", watched > TimeSpan.Zero
                ? "Nothing found: StallWatch was watching and saw no stalls, freezes, input delay or leaks."
                : "No data for this period: StallWatch was not running."]));
        _timeline.EndUpdate();

        if (selectedKey is not null && _timeline.Items.IndexOfKey(selectedKey) is var i and >= 0) _timeline.Items[i].Selected = true;
        else if (_timeline.Items.Count > 0 && _timeline.Items[0].Tag is not null) _timeline.Items[0].Selected = true;
        ShowDetails();
        FillPrograms(findings);
    }

    void ShowDetails()
    {
        if (_timeline.SelectedItems.Count == 0 || _timeline.SelectedItems[0].Tag is not List<Finding> g) { _details.Text = ""; return; }
        var f = g[0];
        var lines = new List<string> { $"{HtmlReport.Label(f.Kind)} · {f.Time.ToString("dddd yyyy-MM-dd HH:mm:ss", Inv)}", "", f.Summary };
        if (f.Evidence.Count > 0) { lines.Add(""); lines.AddRange(f.Evidence.Select(e => "• " + e)); }
        if (g.Count > 1)
        {
            lines.Add("");
            lines.Add($"Happened {g.Count} times that day: {string.Join(", ", g.Select(x => x.Time.ToString("HH:mm:ss", Inv)).Reverse())}");
        }
        _details.Text = string.Join(Environment.NewLine, lines);
    }

    void FillPrograms(List<Finding> findings)
    {
        _programs.BeginUpdate();
        _programs.Items.Clear();
        foreach (var g in findings.Where(f => f.Process is not null)
                     .GroupBy(f => f.Process!, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count()))
        {
            var what = string.Join(", ", g.GroupBy(f => f.Kind).OrderByDescending(x => x.Count()).Select(x => $"{HtmlReport.Label(x.Key)} ×{x.Count()}"));
            _programs.Items.Add(new ListViewItem([g.Key, g.Count().ToString(Inv), what, g.Max(f => f.Time).ToString("yyyy-MM-dd HH:mm", Inv)]));
        }
        _programs.EndUpdate();
    }

    void ExportHtml()
    {
        try
        {
            var path = ReportWriter.Write(_store, Days);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not write the report: " + e.Message, "StallWatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    static string DayTitle(DateTime day) =>
        day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("dddd yyyy-MM-dd", Inv);

    static string Hours(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours.ToString("F1", Inv)} h" : $"{t.TotalMinutes.ToString("F0", Inv)} min";

    // Same colours as the HTML report.
    public static Color KindColor(FindingKind k) => k switch
    {
        FindingKind.DiskStall => Color.FromArgb(0xc2, 0x41, 0x0c),
        FindingKind.FlushStorm => Color.FromArgb(0xa2, 0x1c, 0xaf),
        FindingKind.ResourceLeak => Color.FromArgb(0x0e, 0x74, 0x90),
        FindingKind.FrameFreeze => Color.FromArgb(0xdc, 0x26, 0x26),
        FindingKind.FrameStutter => Color.FromArgb(0xd9, 0x77, 0x06),
        FindingKind.InputDelay => Color.FromArgb(0x25, 0x63, 0xeb),
        FindingKind.AccessibilityClient => Color.FromArgb(0x4d, 0x7c, 0x0f),
        FindingKind.NetworkLag => Color.FromArgb(0x0f, 0x76, 0x6e),
        _ => Color.Gray,
    };

    static Bitmap Dot(Color c)
    {
        var bmp = new Bitmap(12, 12);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var b = new SolidBrush(c);
        g.FillEllipse(b, 1, 1, 10, 10);
        return bmp;
    }
}
