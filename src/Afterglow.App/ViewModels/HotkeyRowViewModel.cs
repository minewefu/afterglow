using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.App.Services;
using Afterglow.Core.Hotkeys;

namespace Afterglow.App.ViewModels;

/// <summary>One action in Settings › Keyboard shortcuts: its keys, why they may not work, and editing.</summary>
public sealed partial class HotkeyRowViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    public HotkeyRowViewModel(SettingsViewModel owner, HotkeyAction action)
    {
        _owner = owner;
        Action = action;
        Title = HotkeyActions.Describe(action);
    }

    public HotkeyAction Action { get; }

    public string Title { get; }

    /// <summary>For a profile slot, which saved profile it applies right now.</summary>
    [ObservableProperty] public partial string Detail { get; set; } = string.Empty;

    /// <summary>Keycaps ("Ctrl", "Alt", "O"); empty when the action has no shortcut.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoKeys))]
    public partial IReadOnlyList<string> Keys { get; set; } = [];

    public bool HasNoKeys => Keys.Count == 0 && !IsRecording;

    /// <summary>Information, e.g. which stand-in a default uses on this keyboard.</summary>
    [ObservableProperty] public partial string Note { get; set; } = string.Empty;

    /// <summary>Why the shortcut doesn't work, or (until the next refresh) why a just-pressed one was refused.</summary>
    [ObservableProperty] public partial string Warning { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoKeys))]
    [NotifyPropertyChangedFor(nameof(ChangeLabel))]
    public partial bool IsRecording { get; set; }

    public string ChangeLabel => IsRecording ? "Cancel" : "Change";

    [RelayCommand]
    private void Change() => _owner.BeginRecording(this);

    [RelayCommand]
    private void Remove() => _owner.SetShortcut(this, string.Empty);

    internal void Show(HotkeyStatus? status)
    {
        Keys = status?.Chord?.Parts ?? [];

        // "Turned off" would repeat on every row under the switch that says so.
        Note = status is { IsProblem: false } && status.Note != HotkeyPlanner.TurnedOff ? status.Note : string.Empty;
        Warning = status is { IsProblem: true } ? status.Note : string.Empty;
    }
}
