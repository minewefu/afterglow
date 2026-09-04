using System.Runtime.InteropServices;
using System.Windows;

namespace Afterglow.App;

/// <summary>
/// Clipboard writes that cannot take the app down.
///
/// <para>
/// The Windows clipboard is a single machine-wide resource that any process can
/// hold open. When one does, <see cref="Clipboard.SetText(string)"/> throws
/// <see cref="COMException"/> (CLIPBRD_E_CANT_OPEN) after its internal retries —
/// routine contention, not a fault in this app, and something a password
/// manager or a remote-desktop client causes regularly. Every call site here
/// sits inside a <c>[RelayCommand]</c>, where an escaping exception becomes an
/// unhandled dispatcher exception: a modal crash dialog raised because a copy
/// button momentarily lost a race.
/// </para>
/// </summary>
internal static class ClipboardSafe
{
    /// <summary>
    /// Copies <paramref name="text"/>, returning false if the clipboard was
    /// unavailable. Failure is logged, never thrown: the user can press the
    /// button again.
    /// </summary>
    internal static bool Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            Core.Diagnostics.Log.Warn($"Clipboard copy failed (another application is holding it): {ex.Message}");
            return false;
        }
    }
}
