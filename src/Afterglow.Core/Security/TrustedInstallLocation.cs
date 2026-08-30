using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Afterglow.Core.Security;

/// <summary>
/// Verdict from <see cref="TrustedInstallLocation.Check"/>. <see cref="Reason"/>
/// is empty when trusted and names the offending path and principal when not.
/// </summary>
public sealed record InstallLocationTrust(bool IsTrusted, string Reason);

/// <summary>
/// Answers one question: can anything other than an administrator change this
/// executable? An elevated no-UAC logon task inherits the trust of the file it
/// points at, so whatever can overwrite that file gets elevation and boot
/// persistence for free. Fails closed — a path or ACL we cannot read counts as
/// untrusted.
/// </summary>
public static class TrustedInstallLocation
{
    /// <summary>Raw GENERIC_ALL / GENERIC_WRITE, as they appear in ACEs nothing has mapped yet.</summary>
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;

    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;

    /// <summary>NT SERVICE\TrustedInstaller: owns most of Program Files, and is not a WellKnownSidType.</summary>
    private const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>
    /// Rights on the exe itself (or the folder holding it) that let a principal
    /// replace it. WRITE_DAC and WRITE_OWNER are in the set because either one
    /// buys all the others.
    /// </summary>
    public const FileSystemRights ReplaceRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
        GenericAll | GenericWrite;

    /// <summary>
    /// Rights on an ancestor folder that let a principal delete or repoint what
    /// lives inside it. Creating new entries alongside the install is harmless,
    /// which is why "Modify" on a data-drive root is not fatal but
    /// FILE_DELETE_CHILD, WRITE_DAC and WRITE_OWNER are.
    /// </summary>
    public const FileSystemRights HijackRights =
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll;

    /// <summary>Administrators, SYSTEM and TrustedInstaller are already above the elevation boundary.</summary>
    public static bool IsTrustedPrincipal(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        // CREATOR OWNER is never present in a token, so an effective ACE for it grants nobody.
        sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) ||
        string.Equals(sid.Value, TrustedInstallerSid, StringComparison.Ordinal);

    /// <summary>
    /// First principal that <paramref name="rules"/> grants any of
    /// <paramref name="dangerous"/> to, or null when only trusted principals can.
    /// Inherit-only ACEs (CREATOR OWNER, the (OI)(CI)(IO) entries on a drive
    /// root) do not apply to the object itself and are skipped. Deny ACEs are
    /// ignored on purpose: a deny can only subtract access, and letting one
    /// rescue an untrusted allow would require the full ACE-order and group
    /// evaluation we cannot do offline.
    /// </summary>
    public static string? FindUntrustedGrant(IReadOnlyList<FileSystemAccessRule> rules, FileSystemRights dangerous)
    {
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 ||
                (rule.FileSystemRights & dangerous) == 0)
            {
                continue;
            }

            if (rule.IdentityReference is not SecurityIdentifier sid || !IsTrustedPrincipal(sid))
            {
                return Describe(rule.IdentityReference);
            }
        }

        return null;
    }

    /// <summary>"BUILTIN\Users" reads better in a refusal than a raw SID.</summary>
    public static string Describe(IdentityReference identity)
    {
        try
        {
            return identity.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return identity.Value;
        }
    }

    /// <summary>
    /// Checks the exe, its folder, and every folder above it. The path is
    /// deliberately not link-resolved: the logon task will run this path, so
    /// this is the chain an attacker has to control — and opening an ACL through
    /// a junction carries an ACL of its own, and whoever created it owns it —
    /// so a redirected path is refused on the junction's own permissions.
    /// </summary>
    public static InstallLocationTrust Check(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new InstallLocationTrust(false, "the executable path is unknown");
        }

        try
        {
            string full = Path.GetFullPath(exePath);
            if (!File.Exists(full))
            {
                return Directory.Exists(full)
                    ? new InstallLocationTrust(false, string.Create(
                        CultureInfo.InvariantCulture, $"{full} is a directory, not an executable"))
                    : new InstallLocationTrust(false, string.Create(
                        CultureInfo.InvariantCulture, $"{full} does not exist"));
            }

            if (Inspect(full, isDirectory: false, ReplaceRights) is { } fileProblem)
            {
                return new InstallLocationTrust(false, fileProblem);
            }

            if (Directory.GetParent(full) is not { } folder)
            {
                return new InstallLocationTrust(false, string.Create(CultureInfo.InvariantCulture, $"{full} has no containing folder"));
            }

            if (Inspect(folder.FullName, isDirectory: true, ReplaceRights) is { } folderProblem)
            {
                return new InstallLocationTrust(false, folderProblem);
            }

            for (var ancestor = folder.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (Inspect(ancestor.FullName, isDirectory: true, HijackRights) is { } ancestorProblem)
                {
                    return new InstallLocationTrust(false, ancestorProblem);
                }
            }

            return new InstallLocationTrust(true, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   NotSupportedException or PrivilegeNotHeldException or
                                   System.Security.SecurityException or InvalidOperationException)
        {
            return new InstallLocationTrust(false, string.Create(CultureInfo.InvariantCulture,
                $"the permissions of {exePath} could not be read ({ex.GetType().Name})"));
        }
    }

    private static string? Inspect(string path, bool isDirectory, FileSystemRights dangerous)
    {
        const AccessControlSections Sections = AccessControlSections.Access | AccessControlSections.Owner;
        FileSystemSecurity security = isDirectory
            ? new DirectorySecurity(path, Sections)
            : new FileSecurity(path, Sections);

        // The owner implicitly holds WRITE_DAC, so a standard-user-owned folder
        // is writable by that user whatever its DACL currently says.
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{path} has no readable owner");
        }

        if (!IsTrustedPrincipal(owner))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{path} is owned by {Describe(owner)}, who can rewrite its permissions");
        }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var list = new List<FileSystemAccessRule>(rules.Count);
        foreach (FileSystemAccessRule rule in rules)
        {
            list.Add(rule);
        }

        return FindUntrustedGrant(list, dangerous) is { } who
            ? string.Create(CultureInfo.InvariantCulture, $"{path} can be changed by {who}")
            : null;
    }
}
