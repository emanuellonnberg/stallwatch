namespace StallWatch.Core.Findings;

public enum FindingKind
{
    DiskStall,
    FlushStorm,
    ResourceLeak,
    FrameFreeze,
    FrameStutter,
    InputDelay,
    AccessibilityClient,
    NetworkLag,
}

/// <summary>
/// Something the user should know about, phrased for humans. Evidence holds supporting lines. Process is the program
/// the finding is about (the culprit for disk/leak/accessibility findings, the affected app for frame findings), if any.
/// </summary>
public sealed record Finding(DateTime Time, FindingKind Kind, string Summary, IReadOnlyList<string> Evidence, string? Process = null)
{
    public override string ToString() =>
        $"{Time:HH:mm:ss} [{Kind}] {Summary}" +
        (Evidence.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, Evidence.Select(e => "    " + e)));
}
