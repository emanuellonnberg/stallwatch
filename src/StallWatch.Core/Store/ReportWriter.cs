namespace StallWatch.Core.Store;

public static class ReportWriter
{
    public static string DefaultPath => Path.Combine(FindingStore.DefaultDirectory, "report.html");

    /// <summary>Writes the report for the last <paramref name="days"/> days (today included) and returns its path.</summary>
    public static string Write(FindingStore store, int days, string? path = null)
    {
        var to = DateTime.Today.AddDays(1);
        var from = to.AddDays(-Math.Max(1, days));
        path ??= DefaultPath;
        File.WriteAllText(path, HtmlReport.Render(store.Findings(from, to), store.Sessions(from, to), from, to, DateTime.Now));
        return path;
    }
}
