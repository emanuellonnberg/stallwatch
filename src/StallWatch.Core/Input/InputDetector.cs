using StallWatch.Core.Findings;

namespace StallWatch.Core.Input;

public enum InputKind { Mouse, Keyboard }

public sealed class InputDetectorOptions
{
    /// <summary>Delivery delay that counts as "late" (you start to feel it around here).</summary>
    public double LateMs { get; init; } = 100;

    /// <summary>This many late events within <see cref="Window"/> is a finding...</summary>
    public int LateCount { get; init; } = 3;

    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>...as is any single event this late.</summary>
    public double SevereMs { get; init; } = 250;

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Reports when mouse/keyboard input reaches applications late. Delay = time the event was received by the
/// system minus the time it was delivered to us (measured by <see cref="RawInputMonitor"/>, which receives input
/// after every global input hook has run, so a slow hook in any program shows up here).
/// </summary>
public sealed class InputDetector(InputDetectorOptions options)
{
    public event Action<Finding>? FindingRaised;

    readonly Queue<(DateTime T, InputKind Kind, double Ms)> _late = new();
    DateTime _lastReport = DateTime.MinValue;

    public void Add(DateTime time, InputKind kind, double delayMs)
    {
        if (delayMs < options.LateMs) return;
        _late.Enqueue((time, kind, delayMs));
        while (_late.TryPeek(out var o) && time - o.T > options.Window) _late.Dequeue();

        var trigger = delayMs >= options.SevereMs || _late.Count >= options.LateCount;
        if (!trigger || time - _lastReport < options.Cooldown) return;
        _lastReport = time;
        Report(time);
    }

    /// <summary>Nothing time-driven yet; kept so all detectors share the same shape.</summary>
    public void Tick(DateTime now) { }

    void Report(DateTime time)
    {
        var items = _late.ToList();
        var worst = items.Max(x => x.Ms);
        var kinds = string.Join(" and ", items.Select(x => x.Kind == InputKind.Mouse ? "mouse" : "keyboard").Distinct());
        var summary = $"Your {kinds} input is reaching programs late: {items.Count} events in the last " +
                      $"{options.Window.TotalSeconds:F0} s were delayed {options.LateMs:F0} ms or more, the worst {worst:F0} ms. " +
                      "This is usually a program with a global keyboard/mouse hook that answers slowly " +
                      "(overlays, hotkey tools, remote-desktop clients, some chat apps), or the whole system stalling.";
        var evidence = new List<string>
        {
            $"delays in this window: {string.Join(", ", items.Select(x => $"{x.Ms:F0}"))} ms",
            "to find the program: close recently started apps one at a time and watch whether this stops",
        };
        FindingRaised?.Invoke(new Finding(time, FindingKind.InputDelay, summary, evidence));
    }
}
