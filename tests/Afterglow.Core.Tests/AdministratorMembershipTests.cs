using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Principal;
using Afterglow.Core.Security;

namespace Afterglow.Core.Tests;

/// <summary>
/// R2-A: whether the person signed in is an administrator decides what "Start
/// with Windows" means for them — no prompt at sign-in, or a UAC password prompt
/// and monitoring-only without it. Membership must survive UAC's filtering, and
/// must be the signed-in person's, not whoever typed a password into the prompt.
/// </summary>
public partial class AdministratorMembershipTests
{
    private const string UsersSid = "S-1-5-32-545";

    private static Claim Group(string sid) => new(ClaimTypes.GroupSid, sid);

    private static Claim DenyOnly(string sid) => new(ClaimTypes.DenyOnlySid, sid);

    [Fact]
    public void An_elevated_administrators_token_is_a_member()
    {
        Assert.True(AdministratorMembership.ClaimsHaveAdministrators(
            [Group(AdministratorMembership.AdministratorsSid), Group(UsersSid)]));
    }

    [Fact]
    public void A_uac_filtered_administrators_token_is_still_a_member()
    {
        // UAC marks Administrators deny-only in the token an administrator's
        // unelevated programs get; IsInRole(Administrator) reads false there.
        Assert.True(AdministratorMembership.ClaimsHaveAdministrators(
            [DenyOnly(AdministratorMembership.AdministratorsSid), Group(UsersSid)]));
    }

    [Fact]
    public void A_standard_users_token_is_not_a_member()
    {
        Assert.False(AdministratorMembership.ClaimsHaveAdministrators([Group(UsersSid)]));
        Assert.False(AdministratorMembership.ClaimsHaveAdministrators([DenyOnly(UsersSid)]));
        Assert.False(AdministratorMembership.ClaimsHaveAdministrators(
            [new Claim(ClaimTypes.PrimarySid, AdministratorMembership.AdministratorsSid)]));
    }

    [Fact]
    public void When_this_process_is_the_signed_in_user_its_token_answers()
    {
        bool lookedUp = false;

        bool? answer = AdministratorMembership.Decide(
            "ONEX3\\Sam", "onex3\\sam ",
            () => false,
            _ => { lookedUp = true; return true; });

        Assert.False(answer);
        Assert.False(lookedUp);
    }

    [Fact]
    public void When_an_administrator_typed_their_password_into_uac_the_signed_in_account_is_looked_up()
    {
        // A standard user's Afterglow elevated with an administrator's
        // credentials runs AS that administrator, whose token says nothing about
        // the person the logon task will start Afterglow for.
        bool tokenAsked = false;
        string? lookedUp = null;

        bool? answer = AdministratorMembership.Decide(
            " ONEX3\\Sam ", "ONEX3\\Admin",
            () => { tokenAsked = true; return true; },
            account => { lookedUp = account; return false; });

        Assert.False(answer);
        Assert.False(tokenAsked);
        Assert.Equal("ONEX3\\Sam", lookedUp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Nobody_signed_in_is_unknown(string? session)
    {
        Assert.Null(AdministratorMembership.Decide(
            session, "NT AUTHORITY\\SYSTEM", () => true, _ => true));
    }

    [Fact]
    public void A_failed_lookup_is_unknown_not_standard()
    {
        Assert.Null(AdministratorMembership.Decide("AzureAD\\Sam", "ONEX3\\Admin", () => true, _ => null));
    }

    [Fact]
    public void The_settings_note_names_the_consequence_for_a_standard_account_only()
    {
        Assert.Null(AdministratorMembership.StartWithWindowsNote(true));

        string standard = AdministratorMembership.StartWithWindowsNote(false)!;
        Assert.StartsWith("You're signed in with a standard account", standard, StringComparison.Ordinal);
        Assert.Contains("administrator's password", standard, StringComparison.Ordinal);
        Assert.Contains("only monitors", standard, StringComparison.Ordinal);

        // Unknown: the same consequence, as a condition rather than a claim.
        Assert.StartsWith("If you're signed in with a standard account", AdministratorMembership.StartWithWindowsNote(null),
            StringComparison.Ordinal);
    }

    // --- This machine's real token and accounts ---------------------------------

    [Fact]
    public void Membership_survives_the_filtering_uac_applies_to_an_administrators_token()
    {
        // Build what UAC hands an administrator's unelevated programs from this
        // process's own token: a LUA token with Administrators made deny-only.
        // Run elevated (as on the dev machine), this exercises the deny-only
        // path for real; run as a standard user, both answers are false.
        using var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query);
        bool member = AdministratorMembership.TokenHasAdministrators(current);

        using var filtered = Filtered(current);

        Assert.False(new WindowsPrincipal(filtered).IsInRole(WindowsBuiltInRole.Administrator));
        Assert.Equal(member, AdministratorMembership.TokenHasAdministrators(filtered));
        if (new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.True(member);
            Assert.Contains(filtered.Claims, c =>
                c.Type == ClaimTypes.DenyOnlySid && c.Value == AdministratorMembership.AdministratorsSid);
        }
    }

    [Fact]
    public void The_account_lookup_agrees_with_this_process_token()
    {
        // Two independent sources for the same account: the token LSA built at
        // sign-in, and the SAM's local-group membership. Skipped in effect when
        // the lookup can't answer (no domain controller for a domain account).
        using var current = WindowsIdentity.GetCurrent();
        Assert.NotNull(AdministratorMembership.AdministratorsGroupName());

        bool? lookedUp = AdministratorMembership.LocalGroupsIncludeAdministrators(current.Name);
        if (lookedUp is { } answer)
        {
            Assert.Equal(AdministratorMembership.TokenHasAdministrators(current), answer);
        }
    }

    [Fact]
    public void An_account_that_does_not_exist_is_unknown()
    {
        Assert.Null(AdministratorMembership.LocalGroupsIncludeAdministrators(
            Environment.MachineName + "\\afterglow-no-such-user-" + Guid.NewGuid().ToString("N")[..8]));
    }

    private static unsafe WindowsIdentity Filtered(WindowsIdentity identity)
    {
        var sid = new SecurityIdentifier(AdministratorMembership.AdministratorsSid);
        byte[] binary = new byte[sid.BinaryLength];
        sid.GetBinaryForm(binary, 0);
        fixed (byte* sidBytes = binary)
        {
            var disable = new SidAndAttributes { Sid = (nint)sidBytes };
            if (!CreateRestrictedToken(
                    identity.AccessToken.DangerousGetHandle(), LuaToken, 1, &disable, 0, 0, 0, 0, out nint token))
            {
                throw new InvalidOperationException($"CreateRestrictedToken failed: {Marshal.GetLastPInvokeError()}");
            }

            try
            {
                return new WindowsIdentity(token);
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
    }

    // winnt.h: LUA_TOKEN, the flag for a UAC-style filtered token.
    private const uint LuaToken = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateRestrictedToken(
        nint existingToken, uint flags, uint disableSidCount, SidAndAttributes* sidsToDisable,
        uint deletePrivilegeCount, nint privilegesToDelete, uint restrictedSidCount, nint sidsToRestrict,
        out nint newToken);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
