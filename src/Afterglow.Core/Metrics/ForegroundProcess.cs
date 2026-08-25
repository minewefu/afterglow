using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Afterglow.Core.Metrics;

/// <summary>
/// Resolves the process that owns the foreground window, including the UWP case
/// where the foreground window belongs to ApplicationFrameHost and the real app
/// is a child window with a different PID.
/// </summary>
public static partial class ForegroundProcess
{
    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumChildWindows(nint hwndParent, EnumWindowsProc callback, nint lparam);

    private delegate bool EnumWindowsProc(nint hwnd, nint lparam);

    public static int? GetForegroundProcessId()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (!process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                return (int)pid;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        // UWP: find the child window owned by a different process.
        uint childPid = 0;
        bool Callback(nint child, nint lparam)
        {
            _ = lparam;
            _ = GetWindowThreadProcessId(child, out uint candidate);
            if (candidate != 0 && candidate != pid)
            {
                childPid = candidate;
                return false;
            }

            return true;
        }

        _ = EnumChildWindows(hwnd, Callback, 0);
        return childPid != 0 ? (int)childPid : (int)pid;
    }
}
