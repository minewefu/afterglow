using System.Security.AccessControl;
using System.Security.Principal;
using Afterglow.Core.Security;

namespace Afterglow.Core.Tests;

public class TrustedInstallLocationTests
{
    private const string Administrators = "S-1-5-32-544";
    private const string LocalSystem = "S-1-5-18";
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string CreatorOwner = "S-1-3-0";
    private const string TrustedInstaller =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>An ACE as it would appear on the object itself; InheritOnly marks the (OI)(CI)(IO) shape.</summary>
    private static FileSystemAccessRule Ace(string sid, FileSystemRights rights,
        AccessControlType type = AccessControlType.Allow, bool inheritOnly = false) =>
        new(new SecurityIdentifier(sid), rights,
            inheritOnly ? InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit : InheritanceFlags.None,
            inheritOnly ? PropagationFlags.InheritOnly : PropagationFlags.None,
            type);

    /// <summary>The real DACL shape of a folder under %ProgramFiles% (verified with icacls).</summary>
    private static List<FileSystemAccessRule> ProgramFilesShape() =>
    [
        Ace(TrustedInstaller, FileSystemRights.FullControl),
        Ace(LocalSystem, FileSystemRights.FullControl),
        Ace(Administrators, FileSystemRights.FullControl),
        Ace(Users, FileSystemRights.ReadAndExecute),
        Ace(CreatorOwner, FileSystemRights.FullControl, inheritOnly: true),
    ];

    [Fact]
    public void Program_files_shaped_acl_has_no_untrusted_writer()
    {
        Assert.Null(TrustedInstallLocation.FindUntrustedGrant(
            ProgramFilesShape(), TrustedInstallLocation.ReplaceRights));
    }

    [Fact]
    public void Modify_for_authenticated_users_is_flagged()
    {
        var rules = ProgramFilesShape();
        rules.Add(Ace(AuthenticatedUsers, FileSystemRights.Modify));

        Assert.NotNull(TrustedInstallLocation.FindUntrustedGrant(
            rules, TrustedInstallLocation.ReplaceRights));
    }

    [Fact]
    public void Inherit_only_aces_do_not_apply_to_the_object_itself()
    {
        // (OI)(CI)(IO) Modify is what a drive root carries for its children; it
        // grants nothing on the root, and CREATOR OWNER is inert the same way.
        var rules = ProgramFilesShape();
        rules.Add(Ace(AuthenticatedUsers, FileSystemRights.Modify, inheritOnly: true));

        Assert.Null(TrustedInstallLocation.FindUntrustedGrant(
            rules, TrustedInstallLocation.ReplaceRights));
    }

    [Fact]
    public void A_deny_ace_never_rescues_an_untrusted_allow()
    {
        List<FileSystemAccessRule> rules =
        [
            Ace(AuthenticatedUsers, FileSystemRights.FullControl, AccessControlType.Deny),
            Ace(AuthenticatedUsers, FileSystemRights.Modify),
        ];

        Assert.NotNull(TrustedInstallLocation.FindUntrustedGrant(
            rules, TrustedInstallLocation.ReplaceRights));
    }

    [Fact]
    public void Write_dac_alone_is_enough_to_be_flagged()
    {
        var rules = ProgramFilesShape();
        rules.Add(Ace(AuthenticatedUsers, FileSystemRights.ChangePermissions));

        Assert.NotNull(TrustedInstallLocation.FindUntrustedGrant(
            rules, TrustedInstallLocation.ReplaceRights));
    }

    [Fact]
    public void Creating_entries_in_an_ancestor_is_not_a_hijack_but_deleting_children_is()
    {
        // C:\ grants Authenticated Users (AD) on the root itself — you can make a
        // new folder there, which cannot touch an existing Program Files install.
        var canCreate = ProgramFilesShape();
        canCreate.Add(Ace(AuthenticatedUsers, FileSystemRights.AppendData));
        Assert.Null(TrustedInstallLocation.FindUntrustedGrant(
            canCreate, TrustedInstallLocation.HijackRights));
        Assert.NotNull(TrustedInstallLocation.FindUntrustedGrant(
            canCreate, TrustedInstallLocation.ReplaceRights));

        var canDeleteChildren = ProgramFilesShape();
        canDeleteChildren.Add(Ace(AuthenticatedUsers, FileSystemRights.DeleteSubdirectoriesAndFiles));
        Assert.NotNull(TrustedInstallLocation.FindUntrustedGrant(
            canDeleteChildren, TrustedInstallLocation.HijackRights));
    }

    [Fact]
    public void An_identity_that_is_not_a_sid_is_treated_as_untrusted()
    {
        List<FileSystemAccessRule> rules =
        [
            new(new NTAccount("BUILTIN", "Administrators"), FileSystemRights.FullControl,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow),
        ];

        Assert.NotNull(TrustedInstallLocation.FindUntrustedGrant(
            rules, TrustedInstallLocation.ReplaceRights));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\definitely\not\here\Afterglow.exe")]
    public void Unresolvable_paths_fail_closed(string? path)
    {
        Assert.False(TrustedInstallLocation.Check(path).IsTrusted);
        Assert.NotEmpty(TrustedInstallLocation.Check(path).Reason);
    }

    [Fact]
    public void An_exe_in_a_user_writable_folder_is_refused()
    {
        string dir = Path.Combine(Path.GetTempPath(), "afterglow-trust-" + Guid.NewGuid().ToString("N"));
        string exe = Path.Combine(dir, "Afterglow.exe");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(exe, "not really an exe");

            var trust = TrustedInstallLocation.Check(exe);

            Assert.False(trust.IsTrusted);
            Assert.Contains(dir, trust.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void A_file_under_the_system_directory_is_trusted()
    {
        // The positive control: without it the check could pass by refusing
        // everything. %SystemRoot%\System32 is the canonical admin-only chain.
        string kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");

        var trust = TrustedInstallLocation.Check(kernel32);

        Assert.True(trust.IsTrusted, trust.Reason);
        Assert.Empty(trust.Reason);
    }
}
