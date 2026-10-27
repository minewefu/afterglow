using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core.Profiles;
using Afterglow.Core.Settings;

namespace Afterglow.App.ViewModels;

public partial class ProfilesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly TuningViewModel _tuning;
    private readonly FansViewModel _fans;
    private readonly Func<TuningProfile, Core.Tuning.ApplyResult> _applyFull;

    public ObservableCollection<TuningProfile> Profiles { get; } = [];

    public ObservableCollection<GameRule> GameRules { get; } = [];

    [ObservableProperty]
    public partial string NewRuleExe { get; set; } = string.Empty;

    [ObservableProperty] public partial string NewRuleCapFps { get; set; } = string.Empty;
    [ObservableProperty] public partial int NewRuleVsyncIndex { get; set; }
    [ObservableProperty] public partial bool NewRuleLowLatency { get; set; }

    private Core.Interop.Nvapi.DrsApi? _drs;
    private bool _drsTried;

    private Core.Interop.Nvapi.DrsApi? Drs
    {
        get
        {
            if (!_drsTried)
            {
                _drsTried = true;
                if (!_services.DemoMode)
                {
                    _drs = Core.Interop.Nvapi.DrsApi.TryCreate(out _);
                }
            }

            return _drs;
        }
    }

    [ObservableProperty]
    public partial GameRule? SelectedRule { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadIntoTuningCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkStableCommand))]
    public partial TuningProfile? Selected { get; set; }

    [ObservableProperty]
    public partial string NewProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    private ProfileCertifier? _certifier;

    [ObservableProperty] public partial bool CertifyRunning { get; set; }
    [ObservableProperty] public partial string CertifyPhaseText { get; set; } = string.Empty;
    [ObservableProperty] public partial double CertifyProgress { get; set; }
    /// <summary>The run's whole log, shown by a LogView that appends only new lines (#7).</summary>
    [ObservableProperty] public partial IReadOnlyList<string> CertifyLog { get; set; } = Core.Stress.RunLogLines.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CertifySecondsLabel))]
    public partial double CertifySeconds { get; set; } = 90;

    public bool CanCertify => !_services.DemoMode && _services.IsElevated && _services.Gpus.Count > 0;

    public string CertifyGateText => CanCertify
        ? string.Empty
        : "Certification applies the profile and stress-tests it, so it needs a real GPU and administrator rights.";

    public string CertifySecondsLabel => $"{CertifySeconds:F0} s per mode";

    /// <summary>
    /// Each mode's mark, then why any stored pass no longer counts — a driver
    /// change, or a burn from before R-01's fix (Core, so it is tested).
    /// </summary>
    public string SelectedCertificationText => Selected?.StatusSummary() ?? string.Empty;

    partial void OnSelectedChanged(TuningProfile? value) =>
        OnPropertyChanged(nameof(SelectedCertificationText));

    [RelayCommand]
    private void ToggleCertify()
    {
        if (CertifyRunning)
        {
            _certifier?.Cancel();
            return;
        }

        if (Selected is null || _services.SelectedGpu is not { } gpu)
        {
            StatusText = "Select a profile to certify.";
            return;
        }

        var profile = Selected;

        // The run happens under the profile's own fan policy, through this
        // card's fan service, and puts back what that service was doing (R-11).
        var fans = _services.FanControl.TryGetValue(gpu.Index, out var fanService)
            ? new CertificationFans(fanService, () => Core.Tuning.GameSessionRestore.CaptureFans(
                fanService.Mode, fanService.CommandedDuty, _services.FanSettingsFor(gpu)))
            : null;
        _certifier = new ProfileCertifier(gpu.Tuner, _services.Profiles, gpu.PciBusId, gpu.PciVendorId, fans);
        _certifier.StatusChanged += status =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnCertifierStatus(status));
        CertifyRunning = true;
        _services.Flight?.Marker($"certify-start profile={profile.Name}");
        _certifier.Start(profile, new CertifierOptions { SecondsPerMode = (int)CertifySeconds });
    }

    [RelayCommand]
    private void CopyCertifyLog() => ClipboardSafe.Copy(string.Join(Environment.NewLine, CertifyLog));

    private void OnCertifierStatus(CertifierStatus status)
    {
        CertifyRunning = status.Running;
        CertifyPhaseText = status.Running
            ? $"[{status.ModeIndex + 1}/{status.ModeCount}] {status.Phase} — {status.ModeElapsed:mm\\:ss} / {status.ModeDuration:mm\\:ss}"
            : status.Passed == true
                ? "Certified across all four modes — marked stable."
                : status.Phase == "interrupted"
                    ? "Interrupted — the configuration under test, or the saved profile, changed during the run. " +
                      "Nothing more was stamped or written to the GPU. See the log."
                : status.FailedMode == "fans"
                    ? "Failed before the run: the profile's fan policy could not be applied, so nothing else was. " +
                      "See the log."
                : status.FailedMode is { } failed
                    // Say what happened, not what was intended. This line
                    // asserted a reset for every failure — including the "apply"
                    // path, which attempts none and leaves a partially applied
                    // profile on the card.
                    ? failed == "apply"
                        ? "Failed while applying the profile — it may be partly applied. " +
                          "Reset from the Tuning page before retrying."
                        : status.ResetSucceeded switch
                        {
                            true => $"Failed during {failed} — the GPU was reset to driver defaults.",
                            false => $"Failed during {failed}, and the reset did NOT fully succeed — " +
                                     "this GPU may still be running the failing settings.",
                            null => $"Failed during {failed}.",
                        }
                    : status.Phase;
        CertifyProgress = status.Running && status.ModeDuration.TotalSeconds > 0
            ? Math.Clamp(
                (status.ModeIndex + status.ModeElapsed.TotalSeconds / status.ModeDuration.TotalSeconds) /
                status.ModeCount, 0, 1)
            : status.Passed == true ? 1 : 0;
        CertifyLog = status.Log;

        if (!status.Running)
        {
            _services.Flight?.Marker($"certify-end passed={status.Passed}");
            string? name = Selected?.Name;
            Reload();
            Selected = Profiles.FirstOrDefault(p => p.Name == name);
            OnPropertyChanged(nameof(SelectedCertificationText));
        }
    }

    public ProfilesViewModel(
        AppServices services,
        TuningViewModel tuning,
        FansViewModel fans,
        Func<TuningProfile, Core.Tuning.ApplyResult> applyFull)
    {
        _services = services;
        _tuning = tuning;
        _fans = fans;
        _applyFull = applyFull;
        Reload();
        foreach (var rule in services.Settings.GameRules)
        {
            GameRules.Add(rule);
        }
    }

    public bool HasProfiles => Profiles.Count > 0;

    public string EmptyStateText { get; } =
        "No profiles yet. Dial in values on the Tuning page (and a fan mode on the Fans page), " +
        "then save them here under a name. Profiles are plain JSON files you can back up or share.";

    private void Reload()
    {
        Profiles.Clear();
        foreach (var profile in _services.Profiles.LoadAll())
        {
            Profiles.Add(profile);
        }

        OnPropertyChanged(nameof(HasProfiles));

        if (_services.Profiles.LastLoadErrors.Count > 0)
        {
            // The status line points at the log, so the log has to say which
            // files and why (R-24 made a corrupt profile one of them).
            foreach (var (file, error) in _services.Profiles.LastLoadErrors)
            {
                Core.Diagnostics.Log.Warn($"Profile not loaded: {file}: {error}");
            }

            StatusText = $"{_services.Profiles.LastLoadErrors.Count} profile file(s) could not be loaded (see logs).";
        }
    }

    [RelayCommand]
    private void AddRule()
    {
        string exe = NewRuleExe.Trim();
        if (exe.Length == 0 || Selected is null)
        {
            StatusText = "Pick a profile in the list and enter the game's executable name (e.g. cyberpunk2077.exe).";
            return;
        }

        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            exe += ".exe";
        }

        // Empty, 0 / off, or 1–1000 (R1). Text that is none of those used to be
        // saved as 0 and an out-of-range number clamped, both silently; empty
        // was saved as 0 too, so "leave the cap to the driver" and "off" could
        // not be told apart.
        if (GameRule.ParseFrameCap(NewRuleCapFps, out int? capFps) is string capError)
        {
            StatusText = $"{capError} The rule was not added.";
            return;
        }

        var rule = new GameRule
        {
            ExecutableName = exe,
            ProfileName = Selected.Name,
            FrameCapFps = capFps,
            DriverVsync = NewRuleVsyncIndex switch { 1 => "on", 2 => "off", _ => "default" },
            LowLatency = NewRuleLowLatency,
        };

        // One rule per game: the watcher keys rules by executable, so a second
        // rule for the same exe silently overrode the first while both showed,
        // and removing either cleared the driver settings of both. Adding a rule
        // for a game that has one replaces it — the way a rule is edited.
        var replaced = GameRules
            .Where(r => string.Equals(r.ExecutableName, exe, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var old in replaced)
        {
            GameRules.Remove(old);
        }

        GameRules.Add(rule);
        PersistRules();
        NewRuleExe = string.Empty;
        NewRuleCapFps = string.Empty;
        NewRuleVsyncIndex = 0;
        NewRuleLowLatency = false;

        // What the replaced rule wrote, so an emptied cap takes back the cap it
        // wrote (DrsApi.ReleaseCap). The last one is the one the watcher used.
        var before = replaced.LastOrDefault()?.ToDriverSettings();
        if (before is { AnythingSet: false })
        {
            before = null;
        }

        string drsNote = string.Empty;
        if (rule.ToDriverSettings().AnythingSet)
        {
            drsNote = ApplyRuleDriverSettings(rule, before);
        }
        else if (before is not null)
        {
            // The new rule writes nothing; the old one's settings would outlive it.
            drsNote = ClearRuleDriverSettings(replaced[^1]);
        }

        StatusText = $"{(replaced.Count > 0 ? "Rule replaced. " : string.Empty)}" +
                     $"When {exe} runs, '{rule.ProfileName}' will be applied automatically (reverted on exit).{drsNote}";
    }

    private string ApplyRuleDriverSettings(GameRule rule, Core.Interop.Nvapi.GameDriverSettings? replacing)
    {
        if (Drs is not { } drs)
        {
            return " Driver settings NOT applied — DRS unavailable on this system.";
        }

        var rc = drs.ApplySettings(rule.ExecutableName, rule.ToDriverSettings(), replacing, out string note);
        if (rc == Core.Interop.Nvapi.NvapiStatus.Ok)
        {
            return $" Driver settings applied and {note} (persist in the driver, active whenever the game runs).";
        }

        // Up to 1.2.0-beta.4 every profile/app name reached the driver
        // truncated to its first character, so every create after the first
        // collided on a profile literally named "A" — which read as "drivers
        // reject creating profiles". With full names that collision is gone;
        // if the driver still refuses, say so with its own status, not a theory.
        if (rc is Core.Interop.Nvapi.NvapiStatus.ExecutableNotFound or Core.Interop.Nvapi.NvapiStatus.ProfileNameInUse)
        {
            return $" Driver settings NOT applied: the driver did not accept a profile for {rule.ExecutableName} " +
                   $"({rc}). As a fallback, add the game once in NVIDIA Control Panel (Manage 3D settings → " +
                   "Program Settings), then re-add this rule.";
        }

        return $" Driver settings NOT applied ({rc} {note}).";
    }

    [RelayCommand]
    private void RemoveRule(GameRule? rule)
    {
        if (rule is null || !GameRules.Remove(rule))
        {
            return;
        }

        PersistRules();
        string drsNote = string.Empty;
        if (rule.ToDriverSettings().AnythingSet)
        {
            drsNote = ClearRuleDriverSettings(rule);
        }

        StatusText = $"Rule removed.{drsNote}";
    }

    /// <summary>
    /// Takes back what one rule wrote to the driver store. Scoped to that rule
    /// (R1): a rule whose cap was empty never removes a cap, and a cap changed
    /// since Afterglow wrote it (in NVIDIA Control Panel, say) is kept and named.
    /// </summary>
    private string ClearRuleDriverSettings(GameRule rule)
    {
        if (Drs is not { } drs)
        {
            return string.Empty;
        }

        return drs.ClearSettings(rule.ExecutableName, rule.ToDriverSettings(), out string note) ==
               Core.Interop.Nvapi.NvapiStatus.Ok
            ? $" Driver settings cleared ({note})."
            : $" Driver settings could not be cleared ({note}).";
    }

    private void PersistRules()
    {
        var rules = GameRules.ToArray();
        _services.UpdateSettings(s => s with { GameRules = rules });
    }

    private bool HasSelection => Selected is not null;

    [RelayCommand]
    private void SaveCurrent()
    {
        string name = string.IsNullOrWhiteSpace(NewProfileName)
            ? $"Profile {DateTime.Now:yyyy-MM-dd HH.mm}"
            : NewProfileName.Trim();

        try
        {
            // Profiles capture the full picture: tuning sliders AND the fan
            // configuration — stamped with the GPU they were saved on, so on a
            // multi-GPU system they can never be applied to the wrong card.
            var (fanMode, fixedPct, curve) = _fans.CurrentConfig;
            var gpu = _services.SelectedGpu;

            // Per-point curve offsets are part of the picture too, on any GPU
            // whose driver answered the live probe: the whole table, explicit
            // zeros included (F-02/F-07), and CapturedVfPoints only when it was
            // actually read. Shared with MCP's save_profile (R-35), which had
            // its own copy of this save without the table.
            var profile = (_tuning.ToProfile(name) with
            {
                FanMode = fanMode,
                FixedFanPct = fixedPct,
                FanCurve = fanMode == FanMode.Curve ? curve : null,
            }).CapturedFrom(gpu);
            _services.Profiles.Save(profile);
            StatusText = $"Saved '{name}' (tuning + {fanMode switch { FanMode.Curve => "fan curve", FanMode.Fixed => "fixed fans", _ => "auto fans" }}).";
            NewProfileName = string.Empty;
            Reload();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ApplySelected()
    {
        if (Selected is null)
        {
            return;
        }

        // The apply callback loads the profile into the Tuning sliders first when it
        // lands on the selected card, and leaves them alone when it lands on another
        // GPU (MainViewModel.ApplyFromProfilesPage, R-49).
        var result = _applyFull(Selected);
        StatusText = $"Applied '{Selected.Name}': {result.Summary}";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MarkStable()
    {
        if (Selected is null)
        {
            return;
        }

        try
        {
            _services.Profiles.Save(Selected with { MarkedStable = true, ModifiedAt = DateTimeOffset.Now });
            StatusText = $"'{Selected.Name}' marked stable — it can now auto-apply at startup (Settings).";
            Reload();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not update: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void LoadIntoTuning()
    {
        if (Selected is null)
        {
            return;
        }

        _tuning.LoadProfile(Selected);
        StatusText = $"Loaded '{Selected.Name}' into the Tuning page (not applied yet).";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected()
    {
        if (Selected is null)
        {
            return;
        }

        _services.Profiles.Delete(Selected.Name);
        StatusText = $"Deleted '{Selected.Name}'.";
        Reload();
    }
}
