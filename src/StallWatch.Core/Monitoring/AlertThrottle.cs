using StallWatch.Core.Findings;

namespace StallWatch.Core.Monitoring;

/// <summary>
/// Decides which findings become a pop-up. A resident monitor that nags gets uninstalled: at most one alert per kind
/// per <see cref="PerKind"/>, and at most one alert of any kind per <see cref="Any"/>. Everything is still saved.
/// </summary>
public sealed class AlertThrottle
{
    public TimeSpan PerKind { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan Any { get; init; } = TimeSpan.FromMinutes(1);

    readonly Dictionary<FindingKind, DateTime> _lastByKind = new();
    DateTime _last = DateTime.MinValue;

    public bool Allow(Finding f)
    {
        if (f.Time - _last < Any) return false;
        if (_lastByKind.TryGetValue(f.Kind, out var t) && f.Time - t < PerKind) return false;
        _last = f.Time;
        _lastByKind[f.Kind] = f.Time;
        return true;
    }
}
