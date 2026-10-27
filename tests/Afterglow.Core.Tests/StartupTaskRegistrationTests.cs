using System.Security.Principal;
using Afterglow.Core.Security;

namespace Afterglow.Core.Tests;

/// <summary>
/// R-46: "Start with Windows" reads the task's real state and each Windows user
/// has their own task. R-41: the task is for the user signed in to the session,
/// not the administrator whose credentials elevated setup. Pure: no task is
/// registered, changed or deleted by these tests.
/// </summary>
public class StartupTaskRegistrationTests
{
    private const string Alice = "S-1-5-21-2951314454-1124991474-1223277341-1001";
    private const string Bob = "S-1-5-21-2951314454-1124991474-1223277341-1005";
    private const string EntraUser = "S-1-12-1-3373225234-1199983843-2147483647-2045226366";
    private const string Exe = @"C:\Program Files\Afterglow\Afterglow.exe";

    private static readonly Dictionary<string, string> Accounts = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"HANDHELD\alice"] = Alice,
        [@"HANDHELD\bob"] = Bob,
        [@"S-CORP\bob"] = Bob,
    };

    private static string? Resolve(string name) => Accounts.GetValueOrDefault(name);

    /// <summary>
    /// The shape `schtasks /Query /TN … /XML` prints on Windows 11 (checked read-only
    /// against an existing per-user logon task): the principal as a SID, the logon
    /// trigger's user as DOMAIN\name, and &lt;Enabled&gt; omitted while true.
    /// </summary>
    private static string Printed(string principal, string triggerUser, string settingsExtra = "", string triggerExtra = "") => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <URI>\Afterglow</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>{principal}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>{settingsExtra}
          </Settings>
          <Triggers>
            <LogonTrigger>{triggerExtra}
              <UserId>{triggerUser}</UserId>
            </LogonTrigger>
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>"{Exe}"</Command>
              <Arguments>--minimized</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    // --- R-46: the toggle reflects the task's actual state ----------------------

    [Fact]
    public void An_enabled_task_as_schtasks_prints_it_starts_at_the_owners_logon()
    {
        var task = RegisteredLogonTask.Parse(Printed(Alice, @"HANDHELD\alice"));

        Assert.NotNull(task);
        Assert.True(task.Enabled);
        Assert.Equal(Exe, task.Command);
        Assert.True(task.StartsAtLogonOf(Alice, Resolve));
    }

    [Fact]
    public void A_task_disabled_in_task_scheduler_is_off()
    {
        // Disabling a task in Task Scheduler adds exactly this to <Settings>
        // (seen read-only on a disabled built-in task); it used to still read "on".
        var task = RegisteredLogonTask.Parse(Printed(Alice, @"HANDHELD\alice", "\n    <Enabled>false</Enabled>"));

        Assert.NotNull(task);
        Assert.False(task.Enabled);
        Assert.False(task.StartsAtLogonOf(Alice, Resolve));
        Assert.True(task.BelongsTo(Alice, Resolve)); // still Alice's to migrate or remove
    }

    [Fact]
    public void A_task_whose_only_logon_trigger_is_disabled_is_off()
    {
        var task = RegisteredLogonTask.Parse(Printed(Alice, @"HANDHELD\alice", triggerExtra: "\n      <Enabled>false</Enabled>"));

        Assert.NotNull(task);
        Assert.True(task.Enabled);
        Assert.False(task.StartsAtLogonOf(Alice, Resolve));
    }

    [Fact]
    public void Another_users_task_is_off_for_this_user_and_not_theirs_to_remove()
    {
        // The pre-R-46 machine-global "Afterglow" task, registered by Bob.
        var task = RegisteredLogonTask.Parse(Printed(Bob, @"HANDHELD\bob"));

        Assert.NotNull(task);
        Assert.True(task.StartsAtLogonOf(Bob, Resolve));
        Assert.False(task.StartsAtLogonOf(Alice, Resolve));
        Assert.False(task.BelongsTo(Alice, Resolve));
    }

    [Theory]
    [InlineData(@"HANDHELD\bob")]
    [InlineData(@"S-CORP\bob")] // a name that merely starts like a SID is still resolved
    [InlineData(Bob)]
    public void A_logon_trigger_for_another_account_is_off(string triggerUser)
    {
        var task = RegisteredLogonTask.Parse(Printed(Alice, triggerUser));

        Assert.NotNull(task);
        Assert.False(task.StartsAtLogonOf(Alice, Resolve));
    }

    [Theory]
    [InlineData(@"OLDNAME\alice")]  // the computer was renamed since: seen on the dev machine
    [InlineData("HANDHELD\\J\uFFFDrg")] // non-ASCII name mangled by the console code page
    public void An_unresolvable_trigger_name_does_not_turn_the_owners_task_off(string triggerUser)
    {
        // The principal's SID still says whose task it is.
        var task = RegisteredLogonTask.Parse(Printed(Alice, triggerUser));

        Assert.NotNull(task);
        Assert.True(task.StartsAtLogonOf(Alice, Resolve));
    }

    [Fact]
    public void What_build_xml_registers_parses_back_as_the_owners_running_task()
    {
        var task = RegisteredLogonTask.Parse(LogonTaskDefinition.BuildXml(Exe, Alice));

        Assert.NotNull(task);
        Assert.True(task.Enabled);
        Assert.Equal(Alice, task.PrincipalUserId);
        Assert.Equal([new LogonTriggerInfo(true, Alice)], task.LogonTriggers);
        Assert.Equal(Exe, task.Command);
        Assert.True(task.StartsAtLogonOf(Alice, Resolve));
        Assert.False(task.StartsAtLogonOf(Bob, Resolve));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Other xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"/>")]
    public void Text_that_is_not_a_task_definition_parses_to_null(string xml) =>
        Assert.Null(RegisteredLogonTask.Parse(xml));

    [Fact]
    public void A_task_without_a_user_principal_belongs_to_nobody()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals><Principal id="Author"><GroupId>S-1-5-32-545</GroupId></Principal></Principals>
              <Triggers><LogonTrigger/></Triggers>
            </Task>
            """;
        var task = RegisteredLogonTask.Parse(xml);

        Assert.NotNull(task);
        Assert.False(task.BelongsTo(Alice, Resolve));
        Assert.False(task.StartsAtLogonOf(Alice, Resolve));
    }

    // --- R-46: one task per account ---------------------------------------------

    [Fact]
    public void Each_account_gets_its_own_task_name()
    {
        Assert.Equal("Afterglow-" + Alice, StartupTaskRegistration.TaskNameFor(Alice));
        Assert.NotEqual(StartupTaskRegistration.TaskNameFor(Alice), StartupTaskRegistration.TaskNameFor(Bob));
        Assert.Equal("Afterglow-" + EntraUser, StartupTaskRegistration.TaskNameFor(EntraUser));
        Assert.Equal(
            ["Afterglow-" + Alice, "Afterglow"],
            StartupTaskRegistration.CandidateTaskNames(Alice));
    }

    [Theory]
    [InlineData("S-1-5-18")]                    // SYSTEM
    [InlineData("S-1-5-19")]                    // LOCAL SERVICE
    [InlineData("S-1-5-32-544")]                // BUILTIN\Administrators
    [InlineData("S-1-1-0")]                     // Everyone
    [InlineData("S-1-5-21-2951314454-1124991474-1223277341")] // a domain, not an account
    [InlineData("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")] // a service SID
    [InlineData("S-1-5-90-0-1")]                // DWM-1
    [InlineData("SY")]                          // an SDDL alias, not a SID
    [InlineData("Afterglow\" /TN \"x")]
    [InlineData(" S-1-5-21-2951314454-1124991474-1223277341-1001")]
    [InlineData("S-1-5-21-2951314454-1124991474-1223277341-1001\n")]
    [InlineData("")]
    public void Only_user_account_sids_can_name_a_task(string sid)
    {
        Assert.False(StartupTaskRegistration.IsUserAccountSid(sid));
        Assert.Throws<ArgumentException>(() => StartupTaskRegistration.TaskNameFor(sid));
    }

    [Theory]
    [InlineData(Alice)]
    [InlineData("S-1-5-21-2951314454-1124991474-1223277341-500")] // the built-in Administrator
    [InlineData(EntraUser)]
    public void Local_domain_and_entra_accounts_are_user_accounts(string sid) =>
        Assert.True(StartupTaskRegistration.IsUserAccountSid(sid));

    [Fact]
    public void The_uninstaller_finds_every_users_task_and_the_legacy_one_but_nothing_else()
    {
        // `schtasks /Query /FO CSV /NH` (shape checked read-only on Windows 11):
        // one row per trigger, so a task can repeat.
        string listing = string.Join("\r\n",
            "\"\\AgentBox-AttachDevDrive\",\"N/A\",\"Ready\"",
            "\"\\Afterglow\",\"N/A\",\"Ready\"",
            $"\"\\Afterglow-{Alice}\",\"N/A\",\"Ready\"",
            $"\"\\Afterglow-{Alice}\",\"N/A\",\"Ready\"",
            $"\"\\Afterglow-{Bob}\",\"N/A\",\"Disabled\"",
            $"\"\\Afterglow-{EntraUser}\",\"N/A\",\"Ready\"",
            "\"\\Afterglow-Updater\",\"N/A\",\"Ready\"",
            "\"\\Afterglow-S-1-5-18\",\"N/A\",\"Ready\"",
            "\"\\AfterglowHelper\",\"N/A\",\"Ready\"",
            "\"\\Vendor\\Afterglow\",\"N/A\",\"Ready\"",
            $"\"\\OneDrive Reporting Task-{Alice}\",\"10/28/2026 11:09:43 AM\",\"Ready\"",
            string.Empty,
            "garbage line");

        Assert.Equal(
            ["Afterglow", "Afterglow-" + Alice, "Afterglow-" + Bob, "Afterglow-" + EntraUser],
            StartupTaskRegistration.AfterglowTasksIn(listing));
    }

    [Fact]
    public void Schtasks_arguments_quote_one_afterglow_task_name()
    {
        string name = StartupTaskRegistration.TaskNameFor(Alice);
        const string xml = @"C:\Windows\Temp\afterglow-task-0123\task.xml";

        Assert.Equal($"/Create /TN \"Afterglow-{Alice}\" /XML \"{xml}\" /F",
            StartupTaskRegistration.CreateArguments(name, xml));
        Assert.Equal($"/Delete /TN \"Afterglow-{Alice}\" /F", StartupTaskRegistration.DeleteArguments(name));
        Assert.Equal($"/Query /TN \"Afterglow-{Alice}\" /XML", StartupTaskRegistration.QueryXmlArguments(name));
        Assert.Equal("/Delete /TN \"Afterglow\" /F", StartupTaskRegistration.DeleteArguments("Afterglow"));
        Assert.Equal("/Query /FO CSV /NH", StartupTaskRegistration.ListArguments);
    }

    [Theory]
    [InlineData("OneDrive Reporting Task-S-1-5-21-2951314454-1124991474-1223277341-1001")]
    [InlineData("Afterglow\" /TN \"Other")]
    [InlineData("Afterglow-Updater")]
    [InlineData("Afterglow- S-1-5-21-2951314454-1124991474-1223277341-1001")]
    [InlineData("")]
    public void Schtasks_arguments_refuse_any_other_task_name(string name)
    {
        Assert.Throws<ArgumentException>(() => StartupTaskRegistration.DeleteArguments(name));
        Assert.Throws<ArgumentException>(() => StartupTaskRegistration.QueryXmlArguments(name));
        Assert.Throws<ArgumentException>(() => StartupTaskRegistration.CreateArguments(name, @"C:\x.xml"));
    }

    [Fact]
    public void Create_arguments_refuse_a_path_that_could_close_the_quote() =>
        Assert.Throws<ArgumentException>(() =>
            StartupTaskRegistration.CreateArguments(StartupTaskRegistration.TaskNameFor(Alice), "C:\\x\" /RU \"SYSTEM"));

    // --- R-41: whose task it is --------------------------------------------------

    [Fact]
    public void Over_the_shoulder_elevation_registers_for_the_signed_in_user_not_the_administrator()
    {
        // Alice (standard) is signed in; setup runs as HANDHELD\bob, the
        // administrator whose password she typed into the UAC prompt.
        var target = StartupTaskRegistration.ResolveTarget(@"HANDHELD\alice", @"HANDHELD\bob", Bob, Resolve);

        Assert.Null(target.Error);
        Assert.Equal(Alice, target.Sid);
        Assert.Equal(@"HANDHELD\alice", target.AccountName);
    }

    [Fact]
    public void A_process_running_as_the_signed_in_user_uses_its_own_sid_without_a_lookup()
    {
        var target = StartupTaskRegistration.ResolveTarget(
            @"AzureAD\AliceExample", @"AZUREAD\aliceexample", EntraUser,
            _ => throw new InvalidOperationException("no lookup expected"));

        Assert.Equal(EntraUser, target.Sid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_nobody_signed_in_there_is_no_one_to_register_for(string? session)
    {
        // A deployment tool running setup as SYSTEM in session 0.
        var target = StartupTaskRegistration.ResolveTarget(session, @"NT AUTHORITY\SYSTEM", "S-1-5-18", Resolve);

        Assert.Null(target.Sid);
        Assert.Contains("No one is signed in", target.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_account_that_does_not_resolve_is_refused()
    {
        var target = StartupTaskRegistration.ResolveTarget(@"HANDHELD\ghost", @"HANDHELD\bob", Bob, Resolve);

        Assert.Null(target.Sid);
        Assert.Contains(@"HANDHELD\ghost", target.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    [InlineData("not a sid")]
    public void An_account_that_resolves_to_anything_but_a_user_is_refused(string resolved)
    {
        var target = StartupTaskRegistration.ResolveTarget(@"HANDHELD\odd", @"HANDHELD\bob", Bob, _ => resolved);

        Assert.Null(target.Sid);
        Assert.NotNull(target.Error);
    }

    [Fact]
    public void The_targets_own_name_resolves_to_its_sid_without_a_lookup()
    {
        var target = StartupTaskRegistration.ResolveTarget(@"HANDHELD\alice", @"HANDHELD\bob", Bob, Resolve);

        Assert.Equal(Alice, target.ResolveAccount(@"handheld\ALICE", _ => null));
        Assert.Null(target.ResolveAccount(@"HANDHELD\bob", _ => null));
    }

    [Fact]
    public void The_live_session_query_names_a_real_user_account_or_nobody()
    {
        // Read-only: WTSQuerySessionInformation on this process's own session.
        // A test host in session 0 (a service) has nobody signed in.
        string? account = InteractiveSessionUser.AccountName();
        if (account is null)
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var target = StartupTaskRegistration.ResolveTarget(
            account, identity.Name, identity.User?.Value, StartupTaskRegistration.ResolveAccountName);

        Assert.Null(target.Error);
        Assert.True(StartupTaskRegistration.IsUserAccountSid(target.Sid));
        Assert.Equal(target.Sid, StartupTaskRegistration.ResolveAccountName(account));
    }
}
