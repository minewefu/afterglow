using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Afterglow.Core.Hotkeys;
using Afterglow.Core.Settings;

namespace Afterglow.App.Services;

/// <summary>One action's shortcut as Settings and the tray show it.</summary>
/// <param name="Chord">The shortcut in use, or the one asked for when none is; null when there is none.</param>
/// <param name="IsActive">Registered: pressing it anywhere runs the action.</param>
/// <param name="Note">What Settings says under the action; empty when there is nothing to explain.</param>
/// <param name="IsProblem">The note says why a shortcut the user expects doesn't work.</param>
public sealed record HotkeyStatus(HotkeyAction Action, HotkeyChord? Chord, bool IsActive, string Note, bool IsProblem);

/// <summary>
/// Global shortcuts via RegisterHotKey, set in Settings (defaults: Ctrl+Alt+O
/// overlay, Ctrl+Alt+R panic reset, Ctrl+Alt+1..5 profile slots). Every shortcut
/// is checked against the user's keyboard layouts before it is registered: a
/// global hotkey takes its keys from every application, and AltGr is Ctrl+Alt to
/// Windows, so Ctrl+Alt+2 used to swallow "@" on Spanish keyboards (issue #6).
/// A default that would type a character falls back to its Ctrl+Alt+F-key
/// stand-in; a shortcut the user chose stays unregistered, with the reason.
/// </summary>
public sealed partial class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;

    /// <summary>Holding the keys fires once, instead of toggling the overlay at key-repeat rate.</summary>
    private const uint ModNoRepeat = 0x4000;

    private const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>Hotkey ids are the action plus this base (applications own 0x0000–0xBFFF).</summary>
    private const int IdBase = 0x100;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);

    private readonly List<int> _registered = [];
    private HwndSource? _source;
    private HotkeySettings _settings = new();
    private nint[] _layouts = [];
    private bool _suspended;

    public event Action? OverlayToggle;

    public event Action? PanicReset;

    public event Action<int>? ApplyProfileSlot;

    /// <summary>Raised on the UI thread after the shortcuts were registered again.</summary>
    public event Action? StatusChanged;

    /// <summary>One entry per <see cref="HotkeyAction"/>, in that order.</summary>
    public IReadOnlyList<HotkeyStatus> Status { get; private set; } = [];

    /// <summary>The status of one action, if the service has planned at least once.</summary>
    public HotkeyStatus? StatusOf(HotkeyAction action) => Status.FirstOrDefault(s => s.Action == action);

    /// <summary>Attach to a window once its handle exists, then register <paramref name="settings"/>.</summary>
    public void Attach(Window window, HotkeySettings settings)
    {
        var helper = new WindowInteropHelper(window);
        nint handle = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        _settings = settings;
        Reregister();
    }

    /// <summary>Registers <paramref name="settings"/> when they differ from what is registered.</summary>
    public void Apply(HotkeySettings settings)
    {
        // Registration belongs to the window's thread; settings can change elsewhere.
        if (_source is { } source && !source.CheckAccess())
        {
            _ = source.Dispatcher.BeginInvoke(() => Apply(settings));
            return;
        }

        if (ReferenceEquals(settings, _settings) && Status.Count > 0)
        {
            return;
        }

        _settings = settings;
        Reregister();
    }

    /// <summary>
    /// Registers again when the installed keyboard layouts changed (one added in
    /// Windows Settings while Afterglow runs). Windows sends no message for that,
    /// so the app calls this from its existing tray-tooltip timer; the check is
    /// one GetKeyboardLayoutList call.
    /// </summary>
    public void RefreshIfLayoutsChanged()
    {
        // As a set: switching layouts can reorder Windows' list (KLF_REORDER),
        // which changes nothing here.
        if (_source is not null && !KeyboardLayouts.Installed().ToHashSet().SetEquals(_layouts))
        {
            Reregister();
        }
    }

    /// <summary>While Settings records a new shortcut: nothing fires, so every key reaches the page.</summary>
    public void Suspend()
    {
        if (!_suspended)
        {
            _suspended = true;
            Reregister();
        }
    }

    public void Resume()
    {
        if (_suspended)
        {
            _suspended = false;
            Reregister();
        }
    }

    /// <summary>Why <paramref name="chord"/> can't be a shortcut on this machine's layouts right now, or null.</summary>
    public static string? Check(HotkeyChord chord)
    {
        nint[] layouts = KeyboardLayouts.Installed();
        return HotkeyPlanner.Check(chord, c => KeyboardLayouts.FirstTyped(c, layouts));
    }

    private void Reregister()
    {
        UnregisterAll();
        nint[] layouts = KeyboardLayouts.Installed();
        _layouts = layouts;
        var plan = HotkeyPlanner.Plan(_settings, chord => KeyboardLayouts.FirstTyped(chord, layouts));

        var status = new List<HotkeyStatus>(plan.Count);
        foreach (var assignment in plan)
        {
            status.Add(Register(assignment));
        }

        Status = status;
        if (_source is not null)
        {
            Core.Diagnostics.Log.Info("Hotkeys: " + string.Join("; ", status.Select(s =>
                $"{s.Action} {(s.IsActive ? s.Chord.ToString() : "inactive")}" + (s.Note.Length > 0 ? $" ({s.Note})" : string.Empty))));
        }

        StatusChanged?.Invoke();
    }

    private HotkeyStatus Register(HotkeyAssignment plan)
    {
        if (plan.Candidates.Count == 0)
        {
            // No shortcut at all, shortcuts turned off, or every candidate refused.
            bool off = plan.Reason == HotkeyPlanner.TurnedOff;
            return new HotkeyStatus(plan.Action, plan.Requested, false, plan.Reason ?? string.Empty,
                IsProblem: plan.Reason is not null && !off);
        }

        if (_source is null)
        {
            // A --screenshot run never attaches: show what would be registered.
            var planned = plan.Candidates[0];
            return new HotkeyStatus(plan.Action, planned, false, Instead(plan, planned, null), IsProblem: false);
        }

        if (_suspended)
        {
            return new HotkeyStatus(plan.Action, plan.Candidates[0], false, "Paused while you set a shortcut", IsProblem: false);
        }

        int id = IdBase + (int)plan.Action;
        string? refused = null;
        foreach (var chord in plan.Candidates)
        {
            if (RegisterHotKey(_source.Handle, id, (uint)chord.Modifiers | ModNoRepeat, chord.VirtualKey))
            {
                _registered.Add(id);
                return new HotkeyStatus(plan.Action, chord, true, Instead(plan, chord, refused), IsProblem: false);
            }

            int error = Marshal.GetLastPInvokeError();
            refused ??= error == ErrorHotkeyAlreadyRegistered
                ? $"{chord} is already taken by another app"
                : $"Windows didn't register {chord} (error {error})";
        }

        return new HotkeyStatus(plan.Action, plan.Requested ?? plan.Candidates[0], false,
            plan.Reason is { } reason ? $"{reason}; {refused}" : refused!, IsProblem: true);
    }

    /// <summary>Why <paramref name="used"/> stands in for the requested shortcut; empty when it is the requested one.</summary>
    private static string Instead(HotkeyAssignment plan, HotkeyChord used, string? refused) =>
        used == plan.Requested ? string.Empty : $"{plan.Reason ?? refused} — using {used} instead";

    private void UnregisterAll()
    {
        if (_source is { } source)
        {
            foreach (int id in _registered)
            {
                _ = UnregisterHotKey(source.Handle, id);
            }
        }

        _registered.Clear();
    }

    private nint WndProc(nint hwnd, int msg, nint wparam, nint lparam, ref bool handled)
    {
        if (msg == WmHotkey && wparam.ToInt32() - IdBase is int index &&
            index >= 0 && index < HotkeyActions.All.Count)
        {
            var action = (HotkeyAction)index;
            switch (action)
            {
                case HotkeyAction.ToggleOverlay:
                    OverlayToggle?.Invoke();
                    break;
                case HotkeyAction.PanicReset:
                    PanicReset?.Invoke();
                    break;
                default:
                    ApplyProfileSlot?.Invoke(HotkeyActions.SlotOf(action)!.Value);
                    break;
            }

            handled = true;
        }

        return 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
