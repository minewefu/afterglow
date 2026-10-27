using System.Runtime.InteropServices;

namespace Afterglow.Core.Security;

/// <summary>
/// The person signed in to the Windows session this process runs in (R-41).
/// UAC starts an elevated process in the requesting user's session even when
/// the elevation used another account's credentials ("over the shoulder"), so
/// the session's user is the interactive user while the process token is the
/// administrator who typed the password. Unlike Inno's ExecAsOriginalUser /
/// runasoriginaluser, this also holds when setup was started with "Run as
/// administrator" (where Inno has no original-user credentials to return to),
/// and it covers the app's own self-elevation, which the Settings toggle runs
/// under. Session 0 (a deployment tool running setup as SYSTEM) has no user.
/// </summary>
public static partial class InteractiveSessionUser
{
    private const nint CurrentServer = 0;           // WTS_CURRENT_SERVER_HANDLE
    private const uint CurrentSession = 0xFFFFFFFF; // WTS_CURRENT_SESSION
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    /// <summary>DOMAIN\name of the session's user, or null when nobody is signed in or the query fails.</summary>
    public static string? AccountName()
    {
        string? user = Query(WtsUserName);
        if (string.IsNullOrWhiteSpace(user))
        {
            return null;
        }

        string? domain = Query(WtsDomainName);
        return string.IsNullOrWhiteSpace(domain) ? user : domain + "\\" + user;
    }

    private static unsafe string? Query(int infoClass)
    {
        try
        {
            nint buffer;
            uint bytes;
            if (WTSQuerySessionInformationW(CurrentServer, CurrentSession, infoClass, &buffer, &bytes) == 0 ||
                buffer == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int WTSQuerySessionInformationW(
        nint server, uint sessionId, int infoClass, nint* buffer, uint* bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void WTSFreeMemory(nint memory);
}
