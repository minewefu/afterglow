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
    private string _newRuleExe = string.Empty;

    [ObservableProperty] private string _newRuleCapFps = string.Empty;
    [ObservableProperty] private int _newRuleVsyncIndex;
    [ObservableProperty] private bool _newRuleLowLatency;

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

    private static Core.Interop.Nvapi.GameDriverSettings ToDriverSettings(GameRule rule) => new()
    {
        FrameCapFps = rule.FrameCapFps,
        Vsync = rule.DriverVsync,
        LowLatency = rule.LowLatency,
    };

    [ObservableProperty]
    private GameRule? _selectedRule;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadIntoTuningCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkStableCommand))]
    private TuningProfile? _selected;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    private ProfileCertifier? _certifier;

    [ObservableProperty] private bool _certifyRunning;
    [ObservableProperty] private string _certifyPhaseText = string.Empty;
    [ObservableProperty] private double _certifyProgress;
    [ObservableProperty] private string _certifyLog = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CertifySecondsLabel))]
    private double _certifySeconds = 90;

    public bool CanCertify => !_services.DemoMode && _services.IsElevated && _services.Gpus.Count > 0;

    public string CertifyGateText => CanCertify
        ? string.Empty
        : "Certification applies the profile and stress-tests it, so it needs a real GPU and administrator rights.";

    public string CertifySecondsLabel => $"{CertifySeconds:F0} s per mode";

    public string SelectedCertificationText => Selected is not { } p
        ? string.Empty
        : string.Join("   ", CertificationModes.All.Select(mode =>
            p.ValidCertification(mode) is { } cert
                ? $"{mode} ✓ {cert.PassedAt:MM-dd}"
                : p.OffsetMatchedCertification(mode) is not null
                    ? $"{mode} ⚠ driver changed"
                    : $"{mode} —"));

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
        _certifier = new ProfileCertifier(gpu.Tuner, _services.Profiles, gpu.PciBusId, gpu.PciVendorId);
        _certifier.StatusChanged += status =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnCertifierStatus(status));
        CertifyRunning = true;
        _services.Flight?.Marker($"certify-start profile={profile.Name}");
        _certifier.Start(profile, new CertifierOptions { SecondsPerMode = (int)CertifySeconds });
    }

    private void OnCertifierStatus(CertifierStatus status)
    {
        CertifyRunning = status.Running;
        CertifyPhaseText = status.Running
            ? $"[{status.ModeIndex + 1}/{status.ModeCount}] {status.Phase} — {status.ModeElapsed:mm\\:ss} / {status.ModeDuration:mm\\:ss}"
            : status.Passed == true
                ? "Certified across all four modes — marked stable."
                : status.FailedMode is { } failed
                    ? $"Failed during {failed} — the GPU was reset to driver defaults."
                    : status.Phase;
        CertifyProgress = status.Running && status.ModeDuration.TotalSeconds > 0
            ? Math.Clamp(
                (status.ModeIndex + status.ModeElapsed.TotalSeconds / status.ModeDuration.TotalSeconds) /
                status.ModeCount, 0, 1)
            : status.Passed == true ? 1 : 0;
        CertifyLog = string.Join(Environment.NewLine, status.Log.TakeLast(10));

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

        int capFps = int.TryParse(NewRuleCapFps.Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int cap)
            ? Math.Clamp(cap, 0, 1000)
            : 0;
        var rule = new GameRule
        {
            ExecutableName = exe,
            ProfileName = Selected.Name,
            FrameCapFps = capFps,
            DriverVsync = NewRuleVsyncIndex switch { 1 => "on", 2 => "off", _ => "default" },
            LowLatency = NewRuleLowLatency,
        };
        GameRules.Add(rule);
        PersistRules();
        NewRuleExe = string.Empty;
        NewRuleCapFps = string.Empty;
        NewRuleVsyncIndex = 0;
        NewRuleLowLatency = false;

        string drsNote = string.Empty;
        if (ToDriverSettings(rule).AnythingSet)
        {
            drsNote = ApplyRuleDriverSettings(rule);
        }

        StatusText = $"When {exe} runs, '{rule.ProfileName}' will be applied automatically (reverted on exit).{drsNote}";
    }

    private string ApplyRuleDriverSettings(GameRule rule)
    {
        if (Drs is not { } drs)
        {
            return " Driver settings NOT applied — DRS unavailable on this system.";
        }

        var rc = drs.ApplySettings(rule.ExecutableName, ToDriverSettings(rule), out string note);
        if (rc == Core.Interop.Nvapi.NvapiStatus.Ok)
        {
            return $" Driver settings applied and {note} (persist in the driver, active whenever the game runs).";
        }

        if (rc is Core.Interop.Nvapi.NvapiStatus.ExecutableNotFound or Core.Interop.Nvapi.NvapiStatus.ProfileNameInUse)
        {
            return $" Driver settings NOT applied: the driver has no profile for {rule.ExecutableName} and " +
                   "current NVIDIA drivers reject creating one via NVAPI. Add the game once in NVIDIA Control " +
                   "Panel (Manage 3D settings → Program Settings), then re-add this rule.";
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
        if (ToDriverSettings(rule).AnythingSet && Drs is { } drs)
        {
            drsNote = drs.ClearSettings(rule.ExecutableName) == Core.Interop.Nvapi.NvapiStatus.Ok
                ? " Driver settings cleared."
                : " Driver settings could not be cleared.";
        }

        StatusText = $"Rule removed.{drsNote}";
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

            // Per-point curve offsets are part of the picture too (20/30/40
            // series): capture whatever is applied right now.
            IReadOnlyDictionary<int, int>? vfPoints = null;
            bool capturedVfPoints = false;
            if (gpu is { Tuner.Capabilities.SupportsVfPoints: true } &&
                gpu.Tuner.TryReadVfPoints(out var tablePoints) == Core.Interop.Nvapi.NvapiStatus.Ok)
            {
                var nonZero = tablePoints.Where(p => p.OffsetMHz != 0)
                    .ToDictionary(p => p.Index, p => p.OffsetMHz);
                vfPoints = nonZero.Count > 0 ? nonZero : null;

                // The table was read, so "none" here is a fact about this card
                // and applying the profile may remove a curve. A profile that
                // never read it says nothing about the curve either way.
                capturedVfPoints = true;
            }

            var profile = _tuning.ToProfile(name) with
            {
                FanMode = fanMode,
                FixedFanPct = fixedPct,
                FanCurve = fanMode == FanMode.Curve ? curve : null,
                GpuUuid = gpu?.Uuid,
                GpuName = gpu?.Name,
                VfPointOffsetsMHz = vfPoints,
                CapturedVfPoints = capturedVfPoints,
            };
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

        var result = _applyFull(Selected);
        _tuning.LoadProfile(Selected);
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
