using System.Globalization;
using StallWatch.Core.Findings;

namespace StallWatch.Core.Accessibility;

/// <summary>
/// Names the program behind each WM_GETOBJECT. A message sent from another thread wakes the receiving thread, and
/// the kernel's ReadyThread event records who woke it, so the sender is whoever readied the probe thread just before
/// the request was seen. Ready events arrive late (ETW buffers), so a request waits until ready events past its time
/// have arrived.
/// </summary>
public sealed class AccessRequestAttributor
{
    public static readonly TimeSpan MatchWindow = TimeSpan.FromMilliseconds(3);
    static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(2);

    readonly List<(DateTime Time, int Pid, string Name)> _readies = new();
    readonly List<AccessRequest> _pending = new();
    readonly Dictionary<string, Client> _clients = new();
    DateTime _latestReady;

    public int Requests { get; private set; }
    public int Unattributed { get; private set; }

    public sealed class Client
    {
        public required string Name { get; init; }
        public required int Pid { get; init; }
        public int Requests { get; set; }
        public SortedSet<string> Apis { get; } = new();
    }

    /// <summary>Something woke the probe thread; pid/name are the waking process.</summary>
    public void Ready(DateTime time, int pid, string name)
    {
        _readies.Add((time, pid, name));
        if (time > _latestReady) _latestReady = time;
        Resolve(false);
    }

    public void Request(AccessRequest r)
    {
        Requests++;
        _pending.Add(r);
        Resolve(false);
    }

    /// <summary>Attributes everything still pending; call once at the end.</summary>
    public IReadOnlyList<Client> Finish()
    {
        Resolve(true);
        return _clients.Values.OrderByDescending(c => c.Requests).ToList();
    }

    void Resolve(bool all)
    {
        for (var i = 0; i < _pending.Count; i++)
        {
            var r = _pending[i];
            if (!all && _latestReady < r.Time + Slack) continue;
            var sender = _readies.LastOrDefault(x => x.Time <= r.Time + Slack && x.Time >= r.Time - MatchWindow);
            if (sender.Name is null) Unattributed++;
            else
            {
                var key = $"{sender.Name} ({sender.Pid})";
                if (!_clients.TryGetValue(key, out var c)) _clients[key] = c = new Client { Name = sender.Name, Pid = sender.Pid };
                c.Requests++;
                c.Apis.Add(r.Api);
            }
            _pending.RemoveAt(i--);
        }
        if (_readies.Count == 0) return;
        var oldestNeeded = (_pending.Count > 0 ? _pending.Min(p => p.Time) : _latestReady) - MatchWindow - MatchWindow;
        _readies.RemoveAll(x => x.Time < oldestNeeded);
    }

    public static Finding ToFinding(DateTime time, IReadOnlyList<Client> clients, TimeSpan watched, int unattributed = 0)
    {
        var summary = clients.Count == 0 && unattributed > 0
            ? $"Programs read this window's UI {unattributed} times in {watched.TotalSeconds:F0} s, but their names are unknown " +
              "(run as administrator to see them). Such accessibility clients switch Chromium/Electron apps into full accessibility mode."
            : clients.Count == 0
            ? $"No program read this window's UI in {watched.TotalSeconds:F0} s, so nothing here should switch Chromium/Electron apps into full accessibility mode."
            : $"{clients.Count} program(s) read other windows' UI: {string.Join(", ", clients.Select(c => c.Name).Distinct())}. " +
              "Accessibility clients like these switch Chromium/Electron apps (VS Code, Edge, Teams, Spotify, Discord) into full " +
              "accessibility mode, which can stall input for everyone while those apps resize or redraw.";
        var evidence = clients.Select(c => string.Create(CultureInfo.InvariantCulture,
            $"{c.Name} (pid {c.Pid}): {c.Requests:N0} requests via {string.Join(" + ", c.Apis)}")).ToList();
        return new Finding(time, FindingKind.AccessibilityClient, summary, evidence, clients.FirstOrDefault()?.Name);
    }
}
