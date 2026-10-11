namespace StallWatch.Tray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // One tray per user session: a second one would fight over the raw-input sink and the ETW session.
        using var single = new Mutex(true, @"Local\StallWatch.Tray", out var first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(startPaused: args.Contains("--paused")));
    }
}
