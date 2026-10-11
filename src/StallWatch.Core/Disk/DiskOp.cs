namespace StallWatch.Core.Disk;

public enum DiskOpKind { Read, Write, Flush }

/// <summary>One completed disk I/O as seen by the kernel (time = completion).</summary>
public sealed record DiskOp(
    DateTime Time,
    int Disk,
    DiskOpKind Kind,
    double ElapsedMs,
    int ProcessId,
    string ProcessName,
    string? FileName,
    long Bytes);

/// <summary>Aggregate of one disk over one wall-clock second.</summary>
public sealed record DiskSecond(
    DateTime Start,
    int Disk,
    int Reads,
    int Writes,
    int Flushes,
    long Bytes,
    double MaxMs,
    double MaxFlushMs);

public sealed class DiskDetectorOptions
{
    /// <summary>Any single I/O or flush slower than this is a stall.</summary>
    public double StallMs { get; init; } = 250;

    /// <summary>A process issuing at least this many flushes per second...</summary>
    public int FlushStormPerSecond { get; init; } = 20;

    /// <summary>...for at least this many consecutive seconds is a flush storm.</summary>
    public int FlushStormSeconds { get; init; } = 10;
}
