using System.ComponentModel;
using System.Diagnostics;
using System.Security;

namespace StallWatch.Tray;

/// <summary>
/// "Start with Windows" as a Task Scheduler task: at logon, for this user, with highest privileges, so disk and frame
/// tracing work without a UAC prompt every login. Imported from XML because the plain schtasks flags give a task that
/// is killed after 72 hours and never starts on battery. Creating or deleting it needs admin, hence one UAC prompt.
/// </summary>
static class LogonTask
{
    public const string Name = "StallWatch";

    public static bool Exists() => Run("schtasks.exe", $"/Query /TN \"{Name}\"", elevate: false) == 0;

    public static bool Install(string exePath)
    {
        var xml = Path.Combine(Path.GetTempPath(), "stallwatch-task.xml");
        File.WriteAllText(xml, TaskXml(exePath, Environment.UserDomainName + "\\" + Environment.UserName), System.Text.Encoding.Unicode);
        try { return Run("schtasks.exe", $"/Create /TN \"{Name}\" /XML \"{xml}\" /F", elevate: true) == 0 && Exists(); }
        finally { File.Delete(xml); }
    }

    public static bool Uninstall() => Run("schtasks.exe", $"/Delete /TN \"{Name}\" /F", elevate: true) == 0 && !Exists();

    static string TaskXml(string exePath, string user) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>StallWatch: finds what makes Windows stutter. Tray icon; findings in %LOCALAPPDATA%\StallWatch.</Description></RegistrationInfo>
          <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT30S</Delay></LogonTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author"><Exec><Command>"{SecurityElement.Escape(exePath)}"</Command></Exec></Actions>
        </Task>
        """;

    /// <returns>Exit code, or -1 if the user declined the UAC prompt.</returns>
    static int Run(string file, string args, bool elevate)
    {
        var psi = new ProcessStartInfo(file, args) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (elevate && !StallWatch.Core.Monitoring.WatchEngine.IsAdmin) { psi.UseShellExecute = true; psi.Verb = "runas"; }
        else psi.UseShellExecute = false;
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception) { return -1; } // UAC declined
    }
}
