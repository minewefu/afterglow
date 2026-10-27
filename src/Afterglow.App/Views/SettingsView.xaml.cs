using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Afterglow.App.ViewModels;
using Afterglow.Core.Hotkeys;

namespace Afterglow.App.Views;

public partial class SettingsView : UserControl
{
    private Window? _window;

    public SettingsView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;

        // Switching to another window ends a recording, so the registered
        // shortcuts are never left paused while the user is elsewhere.
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            _window?.Deactivated += OnWindowDeactivated;
        };
        Unloaded += (_, _) =>
        {
            _window?.Deactivated -= OnWindowDeactivated;
            _window = null;
        };
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) =>
        (DataContext as SettingsViewModel)?.CancelRecording();

    /// <summary>
    /// While a shortcut row is recording, the next non-modifier key press is the
    /// new shortcut. WPF reports Alt combinations and F10 as Key.System, and keys
    /// an IME or a dead key consumed under their own names; all count as pressed.
    /// AltGr arrives as Ctrl+Alt, which is the point: the row then refuses a
    /// chord that types a character.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel { IsRecordingShortcut: true } settings)
        {
            return;
        }

        e.Handled = true;
        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        ModifierKeys pressed = Keyboard.Modifiers;
        if (key == Key.Escape && pressed == ModifierKeys.None)
        {
            settings.CancelRecording();
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (pressed.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (pressed.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (pressed.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        settings.CompleteRecording(
            new HotkeyChord(modifiers, (ushort)KeyInterop.VirtualKeyFromKey(key)),
            withWindowsKey: pressed.HasFlag(ModifierKeys.Windows));
    }

    /// <summary>Clicking elsewhere on the page ends the recording too.</summary>
    private void OnRecordingButtonLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HotkeyRowViewModel { IsRecording: true } } &&
            DataContext is SettingsViewModel settings)
        {
            settings.CancelRecording();
        }
    }
}
