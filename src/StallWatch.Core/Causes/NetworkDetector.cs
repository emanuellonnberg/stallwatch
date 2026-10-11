using System.Globalization;
using StallWatch.Core.Findings;

namespace StallWatch.Core.Causes;

/// <summary>
/// Turns the game-mode pings into findings: replies lost, or a latency spike, within a short window. The router target
/// tells local trouble (Wi-Fi, cable, router) from trouble further out (the internet connection).
/// </summary>
public sealed class NetworkDetector
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(10);
    public int LostCount { get; init; } = 3;
    public double SlowRouterMs { get; init; } = 50;
    public double SlowInternetMs { get; init; } = 150;
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(5);

    public event Action<Finding>? FindingRaised;

    readonly Dictionary<string, List<PingResult>> _byTarget = new();
    readonly Dictionary<string, DateTime> _lastReport = new();

    public void Add(PingResult p)
    {
        if (!_byTarget.TryGetValue(p.Target, out var list)) _byTarget[p.Target] = list = [];
        list.Add(p);
        list.RemoveAll(x => x.Time < p.Time - Window);
        if (_lastReport.TryGetValue(p.Target, out var last) && p.Time - last < Cooldown) return;

        var router = p.Target.StartsWith("router", StringComparison.Ordinal);
        var lost = list.Count(x => x.Ms is null);
        var slowLimit = router ? SlowRouterMs : SlowInternetMs;
        var slow = list.Where(x => x.Ms >= slowLimit).Select(x => x.Ms!.Value).ToList();
        string? what = null;
        if (lost >= LostCount) what = $"{lost} of {list.Count} pings to the {p.Target} were lost in {Window.TotalSeconds:F0} s";
        else if (slow.Count >= 3) what = $"pings to the {p.Target} took up to {slow.Max().ToString("F0", Inv)} ms ({slow.Count} of {list.Count} over {slowLimit.ToString("F0", Inv)} ms)";
        if (what is null) return;

        _lastReport[p.Target] = p.Time;
        var where = router
            ? "Between this PC and your router: Wi-Fi interference/signal, a cable, or the router itself."
            : "Beyond your router (the router answered normally): your internet connection or provider.";
        var otherRouterOk = !router && _byTarget.Where(kv => kv.Key.StartsWith("router", StringComparison.Ordinal))
            .All(kv => kv.Value.All(x => x.Ms is not null && x.Ms < SlowRouterMs));
        FindingRaised?.Invoke(new Finding(p.Time, FindingKind.NetworkLag,
            $"Network trouble: {what}. In online games this shows as lag or rubber-banding. " + (router || otherRouterOk ? where : ""),
            list.Select(x => $"{x.Time.ToString("HH:mm:ss", Inv)} {(x.Ms is { } ms ? ms.ToString("F0", Inv) + " ms" : "lost")}").TakeLast(10).ToList()));
    }
}
