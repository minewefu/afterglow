using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Afterglow.App.Services;

/// <summary>
/// Global hotkeys via RegisterHotKey. Defaults (documented in Settings):
///   Ctrl+Alt+O — toggle overlay
///   Ctrl+Alt+R — panic: reset GPU to driver defaults
///   Ctrl+Alt+1..5 — apply the Nth saved profile (alphabetical)
/// </summary>
public sealed partial class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);

    private readonly List<int> _registered = [];
    private HwndSource? _source;

    public event Action? OverlayToggle;

    public event Action? PanicReset;

    public event Action<int>? ApplyProfileSlot;

    /// <summary>Attach to a window once its handle exists.</summary>
    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        nint handle = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);

        Register(handle, 1, ModControl | ModAlt, 'O');
        Register(handle, 2, ModControl | ModAlt, 'R');
        for (int slot = 0; slot < 5; slot++)
        {
            Register(handle, 10 + slot, ModControl | ModAlt, (uint)('1' + slot));
        }
    }

    private void Register(nint hwnd, int id, uint modifiers, uint key)
    {
        if (RegisterHotKey(hwnd, id, modifiers, key))
        {
            _registered.Add(id);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wparam, nint lparam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wparam.ToInt32();
            switch (id)
            {
                case 1:
                    OverlayToggle?.Invoke();
                    handled = true;
                    break;
                case 2:
                    PanicReset?.Invoke();
                    handled = true;
                    break;
                case >= 10 and < 15:
                    ApplyProfileSlot?.Invoke(id - 10);
                    handled = true;
                    break;
                default:
                    break;
            }
        }

        return 0;
    }

    public void Dispose()
    {
        if (_source is { } source)
        {
            foreach (int id in _registered)
            {
                _ = UnregisterHotKey(source.Handle, id);
            }

            source.RemoveHook(WndProc);
        }

        _registered.Clear();
        _source = null;
    }
}
