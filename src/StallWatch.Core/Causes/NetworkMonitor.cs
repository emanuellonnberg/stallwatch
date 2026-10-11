using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace StallWatch.Core.Causes;

/// <summary>One ping: Ms is null when the reply was lost.</summary>
public readonly record struct PingResult(DateTime Time, string Target, double? Ms);

/// <summary>
/// Pings the default gateway (your router: tells local network/Wi-Fi trouble apart) and one internet host every
/// second. Meant for game mode only; one small packet per target per second.
/// </summary>
public static class NetworkMonitor
{
    public const string InternetHost = "1.1.1.1";
    const int TimeoutMs = 1000;

    public static IPAddress? DefaultGateway() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

    public static async Task RunAsync(Action<PingResult> onPing, CancellationToken ct)
    {
        var gateway = DefaultGateway();
        var targets = new List<(string Label, IPAddress Address)> { ("internet (" + InternetHost + ")", IPAddress.Parse(InternetHost)) };
        if (gateway is not null) targets.Insert(0, ("router (" + gateway + ")", gateway));
        using var pings = new DisposableList<Ping>(targets.Select(_ => new Ping()));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTime.Now;
                var replies = await Task.WhenAll(targets.Select((t, i) => Send(pings[i], t.Address)));
                for (var i = 0; i < targets.Count; i++) onPing(new PingResult(now, targets[i].Label, replies[i]));
            }
        }
        catch (OperationCanceledException) { }
    }

    static async Task<double?> Send(Ping ping, IPAddress address)
    {
        try
        {
            var reply = await ping.SendPingAsync(address, TimeoutMs);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException) { return null; }
    }

    sealed class DisposableList<T>(IEnumerable<T> items) : List<T>(items), IDisposable where T : IDisposable
    {
        public void Dispose() { foreach (var i in this) i.Dispose(); }
    }
}
