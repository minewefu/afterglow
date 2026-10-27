using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Principal;

namespace Afterglow.Core.Security;

/// <summary>
/// Whether the person signed in to this Windows session belongs to
/// BUILTIN\Administrators, which decides what "Start with Windows" means for
/// them (R2-A). The logon task (R-41) runs Afterglow as that person at their
/// highest available level: an administrator's task starts it elevated with
/// no prompt, but a standard account's task can only start it with standard
/// rights, so Afterglow asks for an administrator's password at every sign-in
/// and runs monitoring-only without one. Settings says so before the switch is
/// turned on, not after the first sign-in.
/// <para>
/// Membership, not elevation. UAC signs an administrator in with a filtered
/// token in which Administrators is present but marked deny-only
/// (SE_GROUP_USE_FOR_DENY_ONLY), so <c>IsInRole(Administrator)</c> — the
/// elevation test the rest of Afterglow uses — is false for every administrator
/// whose Afterglow runs unelevated. Here deny-only entries count too; .NET's
/// <see cref="WindowsIdentity.Claims"/> reports them as
/// <see cref="ClaimTypes.DenyOnlySid"/> and enabled groups as
/// <see cref="ClaimTypes.GroupSid"/>.
/// </para>
/// <para>
/// The token only describes the signed-in user while this process runs as
/// them. When a standard user types an administrator's credentials into the
/// UAC prompt — the only way that user gets Afterglow elevated, and the
/// Settings switch needs it elevated — the process token is the
/// administrator's. Then the signed-in account's local groups are looked up
/// with NetUserGetLocalGroups and LG_INCLUDE_INDIRECT, which also counts
/// membership through a domain group. Null when neither can tell (an
/// account the lookup can't resolve, a domain controller out of reach).
/// </para>
/// </summary>
public static partial class AdministratorMembership
{
    /// <summary>BUILTIN\Administrators: a well-known SID, the same in every Windows language.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>
    /// True when the person signed in to this session is an administrator,
    /// false when they are not, null when it can't be told (see the class remarks).
    /// </summary>
    public static bool? IsSignedInUserAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return Decide(
                InteractiveSessionUser.AccountName(), identity.Name,
                () => TokenHasAdministrators(identity), LocalGroupsIncludeAdministrators);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The settings-row note for the signed-in account: null for an
    /// administrator, the standard-account consequence for anyone else, and
    /// the same consequence as a condition when membership is unknown.
    /// </summary>
    public static string? StartWithWindowsNote(bool? signedInUserIsAdministrator) => signedInUserIsAdministrator switch
    {
        true => null,
        false => "You're signed in with a standard account, so Windows will ask for an administrator's password " +
                 "when Afterglow starts at sign-in; without it Afterglow only monitors.",
        null => "If you're signed in with a standard account, Windows will ask for an administrator's password " +
                "when Afterglow starts at sign-in; without it Afterglow only monitors.",
    };

    /// <summary>
    /// The decision, without Windows: the process token answers when this
    /// process runs as the session's user (DOMAIN\name, compared the way
    /// <see cref="StartupTaskRegistration.ResolveTarget"/> compares them);
    /// otherwise the lookup of the session's account does. Null when nobody is
    /// signed in to this session.
    /// </summary>
    internal static bool? Decide(
        string? sessionAccount, string? processAccount, Func<bool> processTokenHasAdministrators,
        Func<string, bool?> accountHasAdministrators)
    {
        ArgumentNullException.ThrowIfNull(processTokenHasAdministrators);
        ArgumentNullException.ThrowIfNull(accountHasAdministrators);
        if (string.IsNullOrWhiteSpace(sessionAccount))
        {
            return null;
        }

        string account = sessionAccount.Trim();
        return string.Equals(account, processAccount?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? processTokenHasAdministrators()
            : accountHasAdministrators(account);
    }

    /// <summary>True when the token's groups hold Administrators, enabled (elevated) or deny-only (UAC-filtered).</summary>
    public static bool TokenHasAdministrators(WindowsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return ClaimsHaveAdministrators(identity.Claims);
    }

    /// <summary>The claims half of <see cref="TokenHasAdministrators"/>, so tests can hand it a filtered token's groups.</summary>
    internal static bool ClaimsHaveAdministrators(IEnumerable<Claim> claims) =>
        claims.Any(c => (c.Type == ClaimTypes.GroupSid || c.Type == ClaimTypes.DenyOnlySid) &&
                        string.Equals(c.Value, AdministratorsSid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether <paramref name="account"/> (DOMAIN\name) is in this computer's
    /// Administrators group, directly or through a domain group; null when the
    /// lookup fails. Compared by the group's own local name, which Windows
    /// translates ("Administratoren" on a German install).
    /// </summary>
    public static bool? LocalGroupsIncludeAdministrators(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (AdministratorsGroupName() is not { } administrators || LocalGroupsOf(account) is not { } groups)
        {
            return null;
        }

        return groups.Any(group => string.Equals(group, administrators, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The local name of BUILTIN\Administrators, without the "BUILTIN\" part; null when it can't be looked up.</summary>
    internal static string? AdministratorsGroupName()
    {
        try
        {
            string qualified = new SecurityIdentifier(AdministratorsSid).Translate(typeof(NTAccount)).Value;
            return qualified[(qualified.LastIndexOf('\\') + 1)..];
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (SystemException ex) when (ex.GetType() == typeof(SystemException))
        {
            // Translate reports any other LSA failure as a bare SystemException.
            return null;
        }
    }

    /// <summary>
    /// NetUserGetLocalGroups(level 0, LG_INCLUDE_INDIRECT) on this computer: the
    /// local groups <paramref name="account"/> belongs to; null on any failure,
    /// including a partial answer.
    /// </summary>
    internal static unsafe IReadOnlyList<string>? LocalGroupsOf(string account)
    {
        nint buffer = 0;
        try
        {
            uint status = NetUserGetLocalGroups(
                null, account, 0, LgIncludeIndirect, &buffer, MaxPreferredLength, out uint read, out uint total);
            if (status != NerrSuccess || read != total)
            {
                return null;
            }

            // An array of LOCALGROUP_USERS_INFO_0, each one LPWSTR.
            var names = new List<string>((int)read);
            for (int i = 0; i < (int)read; i++)
            {
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * nint.Size)) is { } name)
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            // Freed on failure too: the docs require it after ERROR_MORE_DATA.
            if (buffer != 0)
            {
                _ = NetApiBufferFree(buffer);
            }
        }
    }

    // lmaccess.h / lmcons.h / lmerr.h
    private const uint LgIncludeIndirect = 0x1;
    private const uint MaxPreferredLength = 0xFFFFFFFF;
    private const uint NerrSuccess = 0;

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial uint NetUserGetLocalGroups(
        string? serverName, string userName, uint level, uint flags, nint* buffer, uint preferredMaxLength,
        out uint entriesRead, out uint totalEntries);

    [LibraryImport("netapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint NetApiBufferFree(nint buffer);
}
