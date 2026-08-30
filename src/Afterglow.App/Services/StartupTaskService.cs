using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;

namespace Afterglow.App.Services;

/// <summary>
/// Outcome of <see cref="StartupTaskService.Enable"/>. <see cref="Error"/> is
/// null only when the task actually exists afterwards; otherwise it says why
/// not, in words the user can act on.
/// </summary>
public sealed record StartupTaskResult(string? Error)
{
    public bool Created => Error is null;
}

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

    /// <summary>
    /// Refuses unless the exe lives somewhere only administrators can change.
    /// The task starts Afterglow elevated with no consent prompt, so whatever
    /// can replace the exe inherits that at every logon — and the portable build
    /// usually sits in a user-writable folder, where that is a real privilege
    /// crossing rather than a theoretical one.
    /// </summary>
    public static StartupTaskResult Enable()
    {
        string? exe = Environment.ProcessPath;
        using var identity = WindowsIdentity.GetCurrent();
        string? sid = identity.User?.Value;
        if (exe is null || sid is null)
        {
            return new StartupTaskResult("Afterglow couldn't identify its own executable or user account.");
        }

        var trust = Core.Security.TrustedInstallLocation.Check(exe);
        if (!trust.IsTrusted)
        {
            return new StartupTaskResult(
                $"Afterglow won't create an elevated logon task from here: {trust.Reason}. The task would start " +
                "Afterglow with administrator rights and no UAC prompt at every logon, so anything able to replace " +
                "the exe would get those rights too. Install into Program Files (run the setup .exe) and switch " +
                "this on from there.");
        }

        string tmp = Path.Combine(Path.GetTempPath(), $"afterglow-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(tmp, BuildTaskXml(exe, sid), System.Text.Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F") == 0
                ? new StartupTaskResult(null)
                : new StartupTaskResult("Task Scheduler refused to create the startup task.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StartupTaskResult($"Couldn't stage the task definition in {tmp}: {ex.Message}");
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
