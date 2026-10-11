using StallWatch.Core.Findings;
using StallWatch.Core.Store;

namespace StallWatch.Tests;

public sealed class StoreTests : IDisposable
{
    static readonly DateTime T0 = new(2026, 10, 11, 20, 0, 0, DateTimeKind.Local);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "stallwatch-test-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(_dir, "test.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    static Finding Storm(DateTime t) =>
        new(t, FindingKind.FlushStorm, "Code.exe is forcing writes", ["writes to C:\\x\\codebase-external.sqlite (900)"], "Code.exe");

    [Fact]
    public void Findings_round_trip_with_evidence_and_process()
    {
        using (var store = new FindingStore(Db))
        {
            store.Add(Storm(T0));
            store.Add(new Finding(T0.AddMinutes(1), FindingKind.InputDelay, "Mouse late", []));
        }
        using var again = new FindingStore(Db);
        var all = again.Findings(T0.Date, T0.Date.AddDays(1));

        Assert.Equal(2, all.Count);
        Assert.Equal(T0, all[0].Time);
        Assert.Equal("Code.exe", all[0].Process);
        Assert.Contains("codebase-external.sqlite", Assert.Single(all[0].Evidence));
        Assert.Null(all[1].Process);
    }

    [Fact]
    public void Nothing_is_written_until_flushed()
    {
        using var store = new FindingStore(Db);
        store.Add(Storm(T0));
        Assert.Empty(store.Findings(T0.Date, T0.Date.AddDays(1)));
        store.Flush();
        Assert.Single(store.Findings(T0.Date, T0.Date.AddDays(1)));
    }

    [Fact]
    public void Session_end_follows_flushes()
    {
        using var store = new FindingStore(Db);
        store.BeginSession("watch", T0);
        store.Flush(T0.AddMinutes(90));
        var s = Assert.Single(store.Sessions(T0.Date, T0.Date.AddDays(1)));
        Assert.Equal(TimeSpan.FromMinutes(90), s.End - s.Start);
        Assert.Equal("watch", s.Command);
    }

    [Fact]
    public void Overlapping_sessions_count_once()
    {
        var sessions = new List<WatchSession>
        {
            new(1, T0, T0.AddHours(2), "watch"),
            new(2, T0.AddHours(1), T0.AddHours(3), "input"),
        };
        Assert.Equal(TimeSpan.FromHours(3), HtmlReport.Watched(sessions, T0.Date, T0.Date.AddDays(2)));
    }

    [Fact]
    public void Report_lists_programs_and_escapes_text()
    {
        var findings = new List<Finding>
        {
            Storm(T0), Storm(T0.AddMinutes(5)),
            new(T0.AddMinutes(6), FindingKind.FrameFreeze, "DTS.exe froze <800 ms>", [], "DTS.exe"),
        };
        var html = HtmlReport.Render(findings, [new(1, T0, T0.AddHours(1), "watch")], T0.Date, T0.Date.AddDays(1), T0);

        Assert.Contains("Code.exe", html);
        Assert.Contains("Flush storm ×2", html);
        Assert.Contains("DTS.exe froze &lt;800 ms&gt;", html);
        Assert.Contains("1.0 h", html);
    }

    [Fact]
    public void Timeline_folds_repeats_per_day_kind_and_program()
    {
        var findings = new List<Finding>
        {
            Storm(T0), Storm(T0.AddMinutes(5)), Storm(T0.AddMinutes(9)),
            Storm(T0.AddDays(1)),
            new(T0.AddMinutes(6), FindingKind.FrameFreeze, "DTS.exe froze", [], "DTS.exe"),
        };
        var groups = HtmlReport.Fold(findings).ToList();

        // Next day's storm, then today's three storms (newest at +9 min), then the freeze at +6 min.
        Assert.Equal([1, 3, 1], groups.Select(g => g.Count));
        Assert.Equal(T0.AddMinutes(9), groups[1][0].Time);
        Assert.Contains("<b>×3</b>", HtmlReport.Render(findings, [], T0.Date, T0.Date.AddDays(2), T0));
    }

    [Fact]
    public void Report_tells_no_data_from_nothing_found()
    {
        var empty = HtmlReport.Render([], [], T0.Date, T0.Date.AddDays(1), T0);
        var clean = HtmlReport.Render([], [new(1, T0, T0.AddHours(1), "watch")], T0.Date, T0.Date.AddDays(1), T0);
        Assert.Contains("did not run", empty);
        Assert.Contains("Nothing found", clean);
    }
}
