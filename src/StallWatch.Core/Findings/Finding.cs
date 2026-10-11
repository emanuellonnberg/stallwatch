namespace StallWatch.Core.Findings;

public enum FindingKind
{
    DiskStall,
    FlushStorm,
    HandleLeak,
    FrameFreeze,
    InputDelay,
}

/// <summary>Something the user should know about, phrased for humans. Evidence holds supporting lines.</summary>
public sealed record Finding(DateTime Time, FindingKind Kind, string Summary, IReadOnlyList<string> Evidence)
{
    public override string ToString() =>
        $"{Time:HH:mm:ss} [{Kind}] {Summary}" +
        (Evidence.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, Evidence.Select(e => "    " + e)));
}
