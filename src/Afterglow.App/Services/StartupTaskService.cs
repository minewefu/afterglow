using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;

namespace Afterglow.App.Services;

/// <summary>
/// Manages the "Afterglow" Task Scheduler entry that starts the app at logon
/// elevated and without a UAC prompt. A classic Run-key autostart would launch
/// unelevated and re-prompt at every boot; the scheduled task is the standard
/// way resident tuning tools avoid that. Create/delete require the elevated
/// app; querying works from any context.
/// </summary>
public static class StartupTaskService
{
    private const string TaskName = "Afterglow";

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Enable()
    {
        string? exe = Environment.ProcessPath;
        using var identity = WindowsIdentity.GetCurrent();
        string? sid = identity.User?.Value;
        if (exe is null || sid is null)
        {
            return false;
        }

        string tmp = Path.Combine(Path.GetTempPath(), $"afterglow-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(tmp, BuildTaskXml(exe, sid), System.Text.Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F") == 0;
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static bool Disable() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

    /// <summary>
    /// ExecutionTimeLimit PT0S is load-bearing: the scheduler's default 72-hour
    /// limit would kill a resident tray app mid-week.
    /// </summary>
    internal static string BuildTaskXml(string exePath, string userSid) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts Afterglow at logon, elevated, without a UAC prompt.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{userSid}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{userSid}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exePath)}</Command>
              <Arguments>--minimized</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    private static int RunSchtasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return -1;
            }

            if (!process.WaitForExit(15_000))
            {
                process.Kill();
                return -1;
            }

            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
