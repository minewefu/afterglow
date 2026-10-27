using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Afterglow.App.Services;

/// <summary>
/// Tray icon with quick actions: open, toggle overlay, reset to defaults, exit.
/// Also carries alerts as balloon notifications (shown as toasts on Windows 10/11).
/// Talks to Shell_NotifyIcon directly (NOTIFYICON_VERSION_4) instead of going
/// through WinForms' NotifyIcon, which was the only reason the app loaded
/// WinForms at all. The menu is a WPF ContextMenu so it wears the app's theme.
/// </summary>
public sealed partial class TrayService : IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = 0x8000 + 1;   // WM_APP + 1

    private const uint NimAdd = 0x0;
    private const uint NimModify = 0x1;
    private const uint NimDelete = 0x2;
    private const uint NimSetVersion = 0x4;

    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifInfo = 0x10;
    private const uint NifShowTip = 0x80;

    private const uint NiifWarning = 0x2;
    private const uint NotifyIconVersion4 = 4;

    // Version-4 callbacks: LOWORD(lParam) is the event.
    private const int WmContextMenu = 0x007B;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0401;

    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;

    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

    private readonly HwndSource _window;
    private readonly nint _icon;
    private readonly ContextMenu _menu;
    private MenuItem? _overlayItem;
    private MenuItem? _resetItem;
    private readonly Dictionary<string, DateTime> _lastAlertByTitle = [];
    private string _tooltip = "Afterglow";
    private bool _disposed;

    // Logon race: the elevated task can start before Explorer's notification
    // area exists. Retry the add on a timer as well as on TaskbarCreated.
    private readonly System.Windows.Threading.DispatcherTimer _retryAdd = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _retryAttempts;

    public event Action? OpenRequested;

    public event Action? OverlayToggleRequested;

    public event Action? ResetRequested;

    public event Action? ExitRequested;

    public TrayService()
    {
        // A hidden top-level window, not a message-only one: Explorer announces
        // a restart with a broadcast ("TaskbarCreated") that message-only
        // windows never receive, and without it the icon would vanish for good.
        _window = new HwndSource(new HwndSourceParameters("Afterglow tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);

        // An elevated window doesn't receive Explorer's (unelevated) TaskbarCreated
        // broadcast unless it lets the message through UIPI — and the logon task
        // runs elevated. Without this the icon would never come back after an
        // Explorer restart.
        if (TaskbarCreatedMessage != 0)
        {
            _ = ChangeWindowMessageFilterEx(_window.Handle, TaskbarCreatedMessage, MsgFltAllow, 0);
        }

        _retryAdd.Tick += (_, _) =>
        {
            if (_disposed || Add() || ++_retryAttempts >= 60)
            {
                _retryAdd.Stop();
            }
        };

        _icon = LoadIcon();
        _menu = BuildMenu();
        _ = Add();
    }

    private ContextMenu BuildMenu()
    {
        MenuItem Item(string header, Action action, bool isDefault = false)
        {
            var item = new MenuItem { Header = header };
            if (isDefault)
            {
                item.FontWeight = FontWeights.SemiBold;
            }

            item.Click += (_, _) => action();
            return item;
        }

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("Open Afterglow", () => OpenRequested?.Invoke(), isDefault: true));
        _overlayItem = Item("Toggle overlay", () => OverlayToggleRequested?.Invoke());
        menu.Items.Add(_overlayItem);
        menu.Items.Add(new Separator());
        _resetItem = Item("Reset GPU to defaults", () => ResetRequested?.Invoke());
        menu.Items.Add(_resetItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", () => ExitRequested?.Invoke()));
        return menu;
    }

    /// <summary>Shows each item's shortcut while one is registered (it moves with Settings and keyboard layouts).</summary>
    public void SetShortcuts(HotkeyStatus? overlay, HotkeyStatus? reset)
    {
        static string Header(string text, HotkeyStatus? status) =>
            status is { IsActive: true, Chord: { } chord } ? $"{text} ({chord})" : text;

        _overlayItem!.Header = Header("Toggle overlay", overlay);
        _resetItem!.Header = Header("Reset GPU to defaults", reset);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam & 0xFFFF))
            {
                case NinSelect or NinKeySelect:
                    OpenRequested?.Invoke();
                    handled = true;
                    break;
                case WmContextMenu:
                    ShowMenu();
                    handled = true;
                    break;
            }
        }
        else if (TaskbarCreatedMessage != 0 && (uint)msg == TaskbarCreatedMessage)
        {
            // Explorer restarted: its notification area forgot every icon.
            _ = Add();
        }

        return 0;
    }

    private void ShowMenu()
    {
        // Without being the foreground window first, a menu opened from the tray
        // never closes when the user clicks elsewhere (documented Shell behaviour).
        _ = SetForegroundWindow(_window.Handle);
        _menu.IsOpen = true;
    }

    /// <summary>Adds (or re-adds) the icon; on failure retries every 2 s for up to 2 minutes.</summary>
    private unsafe bool Add()
    {
        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        if (!ShellNotifyIcon(NimAdd, &data))
        {
            // Normal while Explorer is still starting at logon.
            if (!_retryAdd.IsEnabled)
            {
                Core.Diagnostics.Log.Info("Tray icon not added yet (notification area unavailable); retrying.");
                _retryAttempts = 0;
                _retryAdd.Start();
            }

            return false;
        }

        data.TimeoutOrVersion = NotifyIconVersion4;
        _ = ShellNotifyIcon(NimSetVersion, &data);
        return true;
    }

    private unsafe NotifyIconData NewData(uint flags)
    {
        var data = new NotifyIconData
        {
            Size = (uint)sizeof(NotifyIconData),
            Window = _window.Handle,
            Id = IconId,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            Icon = _icon,
        };
        Copy(_tooltip, data.Tip, 128);
        return data;
    }

    private static unsafe void Copy(string text, char* destination, int capacity)
    {
        int n = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, n).CopyTo(new Span<char>(destination, capacity));
        destination[n] = '\0';
    }

    /// <summary>
    /// The app icon from the assembly's resources, at the frame closest to the
    /// tray's small-icon size so Windows doesn't rescale a 256 px image into a
    /// blurry 16-24 px one. Falls back to the stock application icon.
    /// </summary>
    private static nint LoadIcon()
    {
        int cx = GetSystemMetrics(SmCxSmIcon), cy = GetSystemMetrics(SmCySmIcon);
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/afterglow.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                if (IconFile.TryCreateIcon(buffer.ToArray(), cx, cy) is { } icon and not 0)
                {
                    return icon;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Core.Diagnostics.Log.Warn($"Tray icon resource unavailable: {ex.Message}");
        }

        return LoadIconW(0, 32512);   // IDI_APPLICATION
    }

    public unsafe void UpdateTooltip(string text)
    {
        // Notification-area tooltips cap at 127 characters.
        text = text.Length > 127 ? text[..127] : text;
        if (_disposed || text == _tooltip)
        {
            return;
        }

        _tooltip = text;
        var data = NewData(NifTip | NifShowTip);
        _ = ShellNotifyIcon(NimModify, &data);
    }

    /// <summary>
    /// Balloon alert, rate-limited to one per minute PER TITLE: a repeating
    /// kind of alert is throttled without silencing a different, possibly more
    /// important one (an automation action, a refused fan command).
    /// </summary>
    public unsafe void Alert(string title, string message)
    {
        var now = DateTime.UtcNow;
        if (_disposed || (_lastAlertByTitle.TryGetValue(title, out var last) && now - last < TimeSpan.FromMinutes(1)))
        {
            return;
        }

        _lastAlertByTitle[title] = now;
        var data = NewData(NifInfo);
        Copy(message, data.Info, 256);
        Copy(title, data.InfoTitle, 64);
        data.InfoFlags = NiifWarning;
        if (!ShellNotifyIcon(NimModify, &data))
        {
            Core.Diagnostics.Log.Warn($"Tray notification not shown: {title} — {message}");
        }
    }

    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _retryAdd.Stop();
        _menu.IsOpen = false;
        var data = NewData(0);
        _ = ShellNotifyIcon(NimDelete, &data);
        _window.RemoveHook(WndProc);
        _window.Dispose();
        _ = DestroyIcon(_icon);
    }

    /// <summary>NOTIFYICONDATAW (shellapi.h), full Vista+ layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ShellNotifyIcon(uint message, NotifyIconData* data);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint LoadIconW(nint instance, nint name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    private const uint MsgFltAllow = 1;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint changeFilterStruct);
}

/// <summary>Picks one frame out of a .ico file and turns it into an HICON.</summary>
internal static partial class IconFile
{
    /// <summary>
    /// The smallest frame at least <paramref name="cx"/> wide (else the largest),
    /// scaled by the system to exactly cx × cy. Null when the bytes are not an icon.
    /// </summary>
    public static nint? TryCreateIcon(byte[] ico, int cx, int cy)
    {
        // ICONDIR: reserved(2) type(2)=1 count(2); ICONDIRENTRY: 16 bytes each.
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 2) != 1)
        {
            return null;
        }

        int count = BitConverter.ToUInt16(ico, 4);
        int best = -1, bestWidth = 0;
        for (int i = 0; i < count && 6 + ((i + 1) * 16) <= ico.Length; i++)
        {
            int width = ico[6 + (i * 16)] is 0 ? 256 : ico[6 + (i * 16)];
            bool better = best < 0 ||
                          (width >= cx && (bestWidth < cx || width < bestWidth)) ||
                          (width < cx && bestWidth < cx && width > bestWidth);
            if (better)
            {
                best = i;
                bestWidth = width;
            }
        }

        if (best < 0)
        {
            return null;
        }

        int entry = 6 + (best * 16);
        int size = BitConverter.ToInt32(ico, entry + 8);
        int offset = BitConverter.ToInt32(ico, entry + 12);
        if (offset < 0 || size <= 0 || (long)offset + size > ico.Length)
        {
            return null;
        }

        // 0x00030000 = the icon-resource format version CreateIconFromResourceEx expects.
        return CreateIconFromResourceEx(ico.AsSpan(offset, size), (uint)size, true, 0x00030000, cx, cy, 0);
    }

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconFromResourceEx(
        ReadOnlySpan<byte> bits, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int cx, int cy, uint flags);
}
