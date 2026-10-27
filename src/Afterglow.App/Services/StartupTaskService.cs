using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Afterglow.Core.Diagnostics;
using Afterglow.Core.Security;

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
/// Manages the Task Scheduler entry that starts the app at logon elevated and
/// without a UAC prompt. A classic Run-key autostart would launch unelevated
/// and re-prompt at every boot; the scheduled task is the standard way
/// resident tuning tools avoid that. Create/delete require the elevated app;
/// querying works from any context.
/// <para>
/// One task per Windows account, "Afterglow-&lt;SID&gt;" (R-46), for the person
/// signed in to this session rather than whoever this process runs as (R-41):
/// see <see cref="StartupTaskRegistration"/> and
/// <see cref="InteractiveSessionUser"/>. The machine-global "Afterglow" task
/// of 1.5.0 and earlier still counts when it is this user's, is replaced by the
/// per-user one on Enable and is removed on Disable; another user's is left alone.
/// </para>
/// </summary>
public static class StartupTaskService
{
    /// <summary>
    /// True only when a task will actually start Afterglow at this user's next
    /// logon — enabled, running as them, with an enabled logon trigger for them
    /// (R-46). It used to be true whenever a task called "Afterglow" existed:
    /// disabled in Task Scheduler, or another user's.
    /// </summary>
    public static bool IsEnabled() => FindStartingTask() is not null;

    /// <summary>
    /// The executable this user's logon task actually launches, or null when
    /// there is no such task (or its definition can't be read). This — not the
    /// exe that happens to be running — is what the elevated-autostart location
    /// check must judge: a dev build or a second copy elsewhere says nothing
    /// about the binary that will start with admin rights at logon.
    /// </summary>
    public static string? RegisteredExecutable() => FindStartingTask()?.Command;

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
        if (exe is null)
        {
            return new StartupTaskResult("Afterglow couldn't identify its own executable.");
        }

        var target = CurrentTarget();
        if (target.Sid is not { } sid)
        {
            return new StartupTaskResult(target.Error ?? "Afterglow couldn't tell which account to start for.");
        }

        var trust = TrustedInstallLocation.Check(exe);
        if (!trust.IsTrusted)
        {
            return new StartupTaskResult(
                $"Afterglow won't create an elevated logon task from here: {trust.Reason}. The task would start " +
                "Afterglow with administrator rights and no UAC prompt at every logon, so anything able to replace " +
                "the exe would get those rights too. Install into Program Files (run the setup .exe) and switch " +
                "this on from there.");
        }

        // Stage the task XML in a freshly created, administrators-only directory
        // under %WINDIR%\Temp instead of the old %TEMP%\afterglow-task-<pid>.xml.
        // The old location was writable by the same user's unelevated processes
        // under a fully predictable name, so a medium-IL process could pre-create
        // or swap the file between our write and the elevated schtasks reading it
        // (TOCTOU, F-33). Here the directory name is random, its DACL grants only
        // SYSTEM + Administrators, and we hold the file open (FileShare.Read) for
        // the whole schtasks call, so there is no window in which anything the
        // standard user controls can influence what schtasks consumes. The hold is
        // a READ handle opened after the write handle is closed: schtasks opens
        // the file for reading with read-only sharing, which a handle with write
        // access would refuse (sharing violation) — so every registration failed.
        string stagingRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        string stagingDir = Path.Combine(stagingRoot, "afterglow-task-" + Guid.NewGuid().ToString("N"));
        string taskName = StartupTaskRegistration.TaskNameFor(sid);
        try
        {
            if (!ProtectedDirectory.TryCreateExclusive(stagingDir, allowUsersRead: false))
            {
                return new StartupTaskResult(
                    "Afterglow couldn't create a private directory to stage the startup task definition.");
            }

            string xmlPath = Path.Combine(stagingDir, "task.xml");
            using (var stream = new FileStream(xmlPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, System.Text.Encoding.Unicode))
            {
                writer.Write(LogonTaskDefinition.BuildXml(exe, sid));
            }

            // Readers welcome, writers and deleters refused until schtasks is done.
            using (new FileStream(xmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (RunSchtasks(StartupTaskRegistration.CreateArguments(taskName, xmlPath)) != 0)
                {
                    return new StartupTaskResult("Task Scheduler refused to create the startup task.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StartupTaskResult($"Couldn't stage the startup task definition: {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(stagingDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Log.Info($"Startup task '{taskName}' registered for {target.AccountName}.");

        // Migration: this user's old machine-global task would otherwise start a
        // second copy at the same logon. A failure here leaves the new task in
        // place (it is what the user asked for) and is only logged.
        _ = RemoveLegacyTaskOf(target);
        return new StartupTaskResult(null);
    }

    /// <summary>
    /// Removes this user's task, and the legacy "Afterglow" task when it is
    /// this user's. True when neither is left; other users' tasks are untouched.
    /// </summary>
    public static bool Disable()
    {
        var target = CurrentTarget();
        if (target.Sid is not { } sid)
        {
            Log.Warn($"Startup task not removed: {target.Error}");
            return false;
        }

        bool removed = true;
        string taskName = StartupTaskRegistration.TaskNameFor(sid);
        if (TryQueryTask(taskName, out _))
        {
            removed = RunSchtasks(StartupTaskRegistration.DeleteArguments(taskName)) == 0;
        }

        return RemoveLegacyTaskOf(target) && removed;
    }

    /// <summary>
    /// The uninstaller's hook (R-46): deletes every user's "Afterglow-&lt;SID&gt;"
    /// task and the legacy "Afterglow" task. No fixed name covers per-user tasks,
    /// so they are enumerated; elevated, schtasks lists every user's tasks. True
    /// when the listing worked and every delete succeeded.
    /// </summary>
    public static bool RemoveAllUsersTasks()
    {
        if (RunSchtasks(StartupTaskRegistration.ListArguments, out string listing) != 0)
        {
            Log.Warn("Couldn't list scheduled tasks to remove Afterglow's startup tasks.");
            return false;
        }

        bool removed = true;
        foreach (string taskName in StartupTaskRegistration.AfterglowTasksIn(listing))
        {
            if (RunSchtasks(StartupTaskRegistration.DeleteArguments(taskName)) == 0)
            {
                Log.Info($"Startup task '{taskName}' removed.");
            }
            else
            {
                Log.Warn($"Couldn't remove startup task '{taskName}'.");
                removed = false;
            }
        }

        return removed;
    }

    private static RegisteredLogonTask? FindStartingTask()
    {
        var target = CurrentTarget();
        if (target.Sid is not { } sid)
        {
            return null;
        }

        var resolve = Resolver(target);
        foreach (string taskName in StartupTaskRegistration.CandidateTaskNames(sid))
        {
            if (TryQueryTask(taskName, out var task) && task is not null && task.StartsAtLogonOf(sid, resolve))
            {
                return task;
            }
        }

        return null;
    }

    /// <summary>True when no legacy task of this user is left (none, someone else's, or deleted now).</summary>
    private static bool RemoveLegacyTaskOf(StartupTaskTarget target)
    {
        if (target.Sid is not { } sid ||
            !TryQueryTask(StartupTaskRegistration.LegacyTaskName, out var legacy) ||
            legacy is null ||
            !legacy.BelongsTo(sid, Resolver(target)))
        {
            return true;
        }

        if (RunSchtasks(StartupTaskRegistration.DeleteArguments(StartupTaskRegistration.LegacyTaskName)) == 0)
        {
            Log.Info($"Legacy startup task '{StartupTaskRegistration.LegacyTaskName}' of {target.AccountName} removed.");
            return true;
        }

        Log.Warn($"Couldn't remove the legacy startup task '{StartupTaskRegistration.LegacyTaskName}'.");
        return false;
    }

    private static StartupTaskTarget CurrentTarget()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return StartupTaskRegistration.ResolveTarget(
            InteractiveSessionUser.AccountName(), identity.Name, identity.User?.Value,
            StartupTaskRegistration.ResolveAccountName);
    }

    private static Func<string, string?> Resolver(StartupTaskTarget target) =>
        name => target.ResolveAccount(name, StartupTaskRegistration.ResolveAccountName);

    /// <summary>False when the task doesn't exist (or can't be read); <paramref name="task"/> is null when its XML didn't parse.</summary>
    private static bool TryQueryTask(string taskName, out RegisteredLogonTask? task)
    {
        task = null;
        if (RunSchtasks(StartupTaskRegistration.QueryXmlArguments(taskName), out string xml) != 0)
        {
            return false;
        }

        task = RegisteredLogonTask.Parse(xml);
        return true;
    }

    private static int RunSchtasks(string arguments) => RunSchtasks(arguments, out _);

    private static int RunSchtasks(string arguments, out string output)
    {
        output = string.Empty;
        try
        {
            // Full path: the elevated app must not run whatever "schtasks.exe"
            // sits in its own (possibly user-writable) folder or working directory.
            using var process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (process is null)
            {
                return -1;
            }

            // Read before waiting: a full pipe would otherwise block the child.
            var reading = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                process.Kill();
                return -1;
            }

            output = reading.Wait(5_000) ? reading.Result : string.Empty;
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
