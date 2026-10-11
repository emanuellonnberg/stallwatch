using StallWatch.Core.Accessibility;

namespace StallWatch.Tests;

public class AccessRequestAttributorTests
{
    static readonly DateTime T0 = new(2026, 10, 11, 20, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Request_is_credited_to_the_process_that_woke_the_thread_just_before()
    {
        var a = new AccessRequestAttributor();
        a.Ready(T0, 100, "csrss.exe");
        a.Ready(T0.AddMilliseconds(500), 4242, "PowerToys.exe");
        a.Request(new AccessRequest(T0.AddMilliseconds(500.4), AccessRequest.UiaRootObjectId));
        a.Ready(T0.AddMilliseconds(600), 100, "csrss.exe");

        var c = Assert.Single(a.Finish());
        Assert.Equal("PowerToys.exe", c.Name);
        Assert.Contains("UI Automation", c.Apis);
    }

    [Fact]
    public void Request_waits_for_late_ready_events()
    {
        // ETW delivers ready events after the window already saw the request.
        var a = new AccessRequestAttributor();
        a.Request(new AccessRequest(T0.AddMilliseconds(10.5), AccessRequest.OBJID_CLIENT));
        a.Ready(T0.AddMilliseconds(10), 7, "LogiTune.exe");
        a.Ready(T0.AddMilliseconds(50), 1, "dwm.exe");

        var c = Assert.Single(a.Finish());
        Assert.Equal("LogiTune.exe", c.Name);
        Assert.Contains("MSAA", c.Apis);
    }

    [Fact]
    public void Request_without_a_recent_wake_is_unattributed()
    {
        var a = new AccessRequestAttributor();
        a.Ready(T0, 7, "LogiTune.exe");
        a.Request(new AccessRequest(T0.AddMilliseconds(300), AccessRequest.OBJID_CLIENT));
        Assert.Empty(a.Finish());
        Assert.Equal(1, a.Unattributed);
    }

    [Fact]
    public void Finding_names_clients_with_counts()
    {
        var a = new AccessRequestAttributor();
        for (var i = 0; i < 3; i++)
        {
            a.Ready(T0.AddSeconds(i), 9, "Dropbox.exe");
            a.Request(new AccessRequest(T0.AddSeconds(i).AddMilliseconds(1), AccessRequest.OBJID_CLIENT));
        }
        var f = AccessRequestAttributor.ToFinding(T0, a.Finish(), TimeSpan.FromSeconds(30));
        Assert.Contains("Dropbox.exe", f.Summary);
        Assert.Contains(f.Evidence, e => e.Contains("3 requests"));
    }
}
