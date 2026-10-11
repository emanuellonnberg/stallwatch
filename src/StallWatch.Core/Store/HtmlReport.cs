using System.Globalization;
using System.Net;
using System.Text;
using StallWatch.Core.Findings;

namespace StallWatch.Core.Store;

/// <summary>
/// A self-contained HTML page (no scripts, no external files) of what StallWatch saw: how long it watched, findings per
/// day by kind, the programs behind most findings, and a timeline with the evidence folded under each finding.
/// </summary>
public static class HtmlReport
{
    public const int TimelineLimit = 500;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Label(FindingKind k) => k switch
    {
        FindingKind.DiskStall => "Disk stall",
        FindingKind.FlushStorm => "Flush storm",
        FindingKind.ResourceLeak => "Leak",
        FindingKind.FrameFreeze => "App froze",
        FindingKind.FrameStutter => "Stutter",
        FindingKind.InputDelay => "Input delay",
        FindingKind.AccessibilityClient => "UI reader",
        FindingKind.NetworkLag => "Network",
        _ => k.ToString(),
    };

    static string Css(FindingKind k) => k switch
    {
        FindingKind.DiskStall => "disk",
        FindingKind.FlushStorm => "flush",
        FindingKind.ResourceLeak => "leak",
        FindingKind.FrameFreeze => "freeze",
        FindingKind.FrameStutter => "stutter",
        FindingKind.InputDelay => "input",
        FindingKind.AccessibilityClient => "a11y",
        FindingKind.NetworkLag => "net",
        _ => "other",
    };

    public static string Render(IReadOnlyList<Finding> findings, IReadOnlyList<WatchSession> sessions, DateTime from, DateTime to, DateTime generated)
    {
        var sb = new StringBuilder();
        string H(string s) => WebUtility.HtmlEncode(s);
        var watched = Watched(sessions, from, to);
        var kinds = findings.Select(f => f.Kind).Distinct().Order().ToList();

        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>StallWatch report</title>
            <style>
            :root { --bg:#fbfbfa; --fg:#1f2328; --muted:#6a737d; --line:#e3e3e0; --card:#fff;
                    --disk:#c2410c; --flush:#a21caf; --leak:#0e7490; --freeze:#dc2626; --stutter:#d97706; --input:#2563eb; --a11y:#4d7c0f; --net:#0f766e; --other:#6b7280; }
            @media (prefers-color-scheme: dark) { :root { --bg:#161718; --fg:#e6e6e3; --muted:#9a9a96; --line:#2c2d2f; --card:#1e1f21; } }
            * { box-sizing:border-box; }
            body { margin:0; background:var(--bg); color:var(--fg); font:15px/1.5 system-ui, "Segoe UI", sans-serif; }
            main { max-width:1000px; margin:0 auto; padding:24px 16px 64px; }
            h1 { font-size:22px; margin:0 0 4px; } h2 { font-size:16px; margin:32px 0 10px; }
            .muted { color:var(--muted); }
            .cards { display:grid; grid-template-columns:repeat(auto-fit, minmax(150px, 1fr)); gap:10px; margin-top:18px; }
            .card { background:var(--card); border:1px solid var(--line); border-radius:8px; padding:10px 12px; }
            .card b { display:block; font-size:22px; font-variant-numeric:tabular-nums; }
            .day { display:grid; grid-template-columns:auto 1fr auto; white-space:nowrap; gap:10px; align-items:center; padding:3px 0; }
            .bar { display:flex; height:14px; border-radius:3px; overflow:hidden; background:var(--line); }
            .bar span { display:block; height:100%; }
            .legend { display:flex; flex-wrap:wrap; gap:6px 14px; margin:8px 0; font-size:13px; }
            .chip { display:inline-block; padding:0 7px; border-radius:10px; color:#fff; font-size:12px; font-weight:600; white-space:nowrap; }
            .disk{background:var(--disk)} .flush{background:var(--flush)} .leak{background:var(--leak)} .freeze{background:var(--freeze)}
            .stutter{background:var(--stutter)} .input{background:var(--input)} .a11y{background:var(--a11y)} .net{background:var(--net)} .other{background:var(--other)}
            table { width:100%; border-collapse:collapse; font-size:14px; }
            th, td { text-align:left; padding:6px 8px; border-bottom:1px solid var(--line); vertical-align:top; }
            td.n { text-align:right; font-variant-numeric:tabular-nums; }
            details { background:var(--card); border:1px solid var(--line); border-radius:8px; padding:8px 12px; margin:6px 0; }
            summary { cursor:pointer; list-style:none; } summary::-webkit-details-marker { display:none; }
            summary time { color:var(--muted); font-variant-numeric:tabular-nums; margin-right:6px; }
            details ul { margin:8px 0 2px; padding-left:20px; color:var(--muted); font-size:14px; overflow-wrap:anywhere; }
            .scroll { overflow-x:auto; }
            </style></head><body><main>
            <h1>StallWatch report</h1>
            <div class="muted">{{from:yyyy-MM-dd}} to {{to.AddSeconds(-1):yyyy-MM-dd}} · generated {{generated:yyyy-MM-dd HH:mm}}</div>
            <div class="cards">
              <div class="card"><b>{{Hours(watched)}}</b>watched</div>
              <div class="card"><b>{{findings.Count.ToString("N0", Inv)}}</b>findings</div>
            """);
        foreach (var k in kinds)
            sb.Append($"""<div class="card"><b>{findings.Count(f => f.Kind == k).ToString("N0", Inv)}</b><span class="chip {Css(k)}">{Label(k)}</span></div>""");
        sb.Append("</div>");

        if (findings.Count == 0)
        {
            sb.Append(watched > TimeSpan.Zero
                ? "<h2>Nothing found</h2><p>StallWatch was watching and saw no stalls, freezes, input delay or leaks in this period.</p>"
                : "<h2>No data</h2><p>StallWatch did not run in this period. Start <code>stallwatch watch</code> (or the tray app) to collect findings.</p>");
            return sb.Append("</main></body></html>").ToString();
        }

        // Per day.
        sb.Append("<h2>Per day</h2><div class=\"legend\">");
        foreach (var k in kinds) sb.Append($"""<span class="chip {Css(k)}">{Label(k)}</span>""");
        sb.Append("</div>");
        var days = findings.GroupBy(f => f.Time.Date).ToDictionary(g => g.Key, g => g.ToList());
        var max = days.Values.Max(d => d.Count);
        for (var day = to.AddSeconds(-1).Date; day >= from.Date; day = day.AddDays(-1))
        {
            var list = days.GetValueOrDefault(day) ?? [];
            var dayWatched = Watched(sessions, day, day.AddDays(1));
            if (list.Count == 0 && dayWatched == TimeSpan.Zero) continue;
            sb.Append($"""<div class="day"><span>{day.ToString("ddd yyyy-MM-dd", Inv)}</span><div class="bar" title="{list.Count} findings">""");
            foreach (var g in list.GroupBy(f => f.Kind).OrderBy(g => g.Key))
                sb.Append($"""<span class="{Css(g.Key)}" style="width:{(100.0 * g.Count() / max).ToString("F1", Inv)}%" title="{Label(g.Key)}: {g.Count()}"></span>""");
            sb.Append($"""</div><span class="muted">{list.Count} · {Hours(dayWatched)}</span></div>""");
        }

        // Programs.
        var programs = findings.Where(f => f.Process is not null)
            .GroupBy(f => f.Process!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Take(15).ToList();
        if (programs.Count > 0)
        {
            sb.Append("<h2>Programs involved</h2><div class=\"scroll\"><table><tr><th>Program</th><th>Findings</th><th>What</th><th>Last</th></tr>");
            foreach (var g in programs)
            {
                sb.Append($"<tr><td>{H(g.Key)}</td><td class=\"n\">{g.Count()}</td><td>");
                foreach (var k in g.GroupBy(f => f.Kind).OrderByDescending(x => x.Count()))
                    sb.Append($"""<span class="chip {Css(k.Key)}">{Label(k.Key)} ×{k.Count()}</span> """);
                sb.Append($"</td><td>{g.Max(f => f.Time):MM-dd HH:mm}</td></tr>");
            }
            sb.Append("</table></div>");
        }

        // Timeline, newest first; repeats of the same kind and program on the same day fold into one entry.
        var groups = Fold(findings).Take(TimelineLimit).ToList();
        sb.Append($"<h2>Timeline</h2>");
        if (groups.Count == TimelineLimit) sb.Append($"<p class=\"muted\">Newest {TimelineLimit} entries.</p>");
        foreach (var g in groups)
        {
            var f = g[0]; // newest
            var when = g.Count == 1
                ? f.Time.ToString("MM-dd HH:mm:ss", Inv)
                : $"{f.Time.ToString("MM-dd", Inv)} {g[^1].Time.ToString("HH:mm", Inv)}–{f.Time.ToString("HH:mm", Inv)}";
            var times = g.Count == 1 ? "" : $" <b>×{g.Count}</b>";
            sb.Append($"""<details><summary><time>{when}</time><span class="chip {Css(f.Kind)}">{Label(f.Kind)}</span>{times} {H(f.Summary)}</summary>""");
            if (f.Evidence.Count > 0 || g.Count > 1)
            {
                sb.Append("<ul>");
                foreach (var e in f.Evidence) sb.Append($"<li>{H(e)}</li>");
                if (g.Count > 1) sb.Append($"<li>{g.Count} times: {string.Join(", ", g.Select(x => x.Time.ToString("HH:mm:ss", Inv)).Reverse())}</li>");
                sb.Append("</ul>");
            }
            sb.Append("</details>");
        }
        return sb.Append("</main></body></html>").ToString();
    }

    /// <summary>Newest first; each group is one day's findings of one kind about one program (newest first inside).
    /// Input delay has no program, so it groups by kind alone.</summary>
    public static IEnumerable<List<Finding>> Fold(IReadOnlyList<Finding> findings) =>
        findings.GroupBy(f => (f.Time.Date, f.Kind, Process: f.Process?.ToLowerInvariant()))
            .Select(g => g.OrderByDescending(f => f.Time).ToList())
            .OrderByDescending(g => g[0].Time);

    /// <summary>Total watched time inside [from, to), overlapping sessions counted once.</summary>
    public static TimeSpan Watched(IReadOnlyList<WatchSession> sessions, DateTime from, DateTime to)
    {
        var total = TimeSpan.Zero;
        var cursor = from;
        foreach (var s in sessions.OrderBy(s => s.Start))
        {
            var start = s.Start < cursor ? cursor : s.Start;
            var end = s.End > to ? to : s.End;
            if (end <= start) continue;
            total += end - start;
            cursor = end;
        }
        return total;
    }

    static string Hours(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours.ToString("F1", Inv)} h" : $"{t.TotalMinutes.ToString("F0", Inv)} min";
}
