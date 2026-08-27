using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core.Settings;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private OverlayWindow? _overlay;
    private (int Core, int Mem, double Power, uint? Boost, uint? Lock)? _preGameState;
    private (Core.Profiles.FanMode Mode, uint FixedPct, Core.Fans.FanCurveConfig Curve)? _preGameFans;

    public DashboardViewModel Dashboard { get; }

    public TuningViewModel Tuning { get; }

    public FansViewModel Fans { get; }

    public MetricsViewModel Metrics { get; }

    public VfCurveViewModel VfCurve { get; }

    public StabilityViewModel Stability { get; }

    public ProfilesViewModel Profiles { get; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private ObservableObject _currentPage;

    [ObservableProperty]
    private string _currentPageName = "dashboard";

    [ObservableProperty]
    private bool _showCrashBanner;

    [ObservableProperty]
    private string _crashBannerText = string.Empty;

    [ObservableProperty]
    private bool _showForensicsBanner;

    [ObservableProperty]
    private string _forensicsBannerText = string.Empty;

    public string GpuName { get; }

    public string DriverText { get; }

    public bool IsElevated => _services.IsElevated;

    public bool IsDemoMode => _services.DemoMode;

    public string ElevationBadge => _services.DemoMode
        ? "DEMO DATA"
        : _services.IsElevated ? "" : "MONITORING ONLY — restart as administrator to tune";

    public bool ShowElevationBadge => !string.IsNullOrEmpty(ElevationBadge);

    public MainViewModel(AppServices services)
    {
        _services = services;

        GpuName = services.DemoMode
            ? "Afterglow Demo GPU"
            : services.Gpus.Count > 0 ? services.Gpus[0].Name : "No NVIDIA GPU detected";
        DriverText = services.DemoMode ? "Synthetic demo data" : $"NVIDIA driver {services.DriverVersion}";

        Dashboard = new DashboardViewModel(services);
        Tuning = new TuningViewModel(services);
        Fans = new FansViewModel(services);
        Metrics = new MetricsViewModel(services);
        VfCurve = new VfCurveViewModel(services);
        Stability = new StabilityViewModel(services);
        Profiles = new ProfilesViewModel(services, Tuning, Fans, p => ApplyProfileFull(p));
        Settings = new SettingsViewModel(services);

        _currentPage = Dashboard;

        CheckCrashRecovery();

        if (services.LastCrashReport is { } crash &&
            DateTimeOffset.Now - crash.CrashedAt < TimeSpan.FromHours(72))
        {
            ShowForensicsBanner = true;
            ForensicsBannerText = $"Last session ended in a crash. {crash.Headline}";
        }

        _automation.UpdateRules(services.Settings.AutomationRules);
        services.SettingsChanged += updated => _automation.UpdateRules(updated.AutomationRules);
        services.Telemetry.SnapshotTaken += snapshot =>
        {
            if (snapshot.DeviceIndex != 0)
            {
                return;
            }

            var fired = _automation.Evaluate(snapshot, DateTimeOffset.Now);
            if (fired.Count > 0)
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    foreach (var automationEvent in fired)
                    {
                        ExecuteAutomation(automationEvent);
                    }
                });
            }
        };

        services.GameWatcher.GameStarted += rule =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnGameStarted(rule));
        services.GameWatcher.GameExited += rule =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnGameExited(rule));
        services.TdrWatchdog.DriverResetDetected += description =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnDriverReset(description));
        services.Telemetry.SnapshotTaken += CheckAlerts;
    }

    /// <summary>Raised for tray balloon alerts (title, message).</summary>
    public event Action<string, string>? TrayAlert;

    /// <summary>Live one-line status for the tray tooltip.</summary>
    public string BuildTrayTooltip()
    {
        var snapshot = _services.Gpus.Count > 0
            ? _services.Telemetry.HistoryFor(_services.Gpus[0].Index).Latest
            : _services.DemoMode ? _services.Telemetry.HistoryFor(0).Latest : null;
        return snapshot is null
            ? "Afterglow"
            : $"Afterglow — {snapshot.GpuTempC}°C · {snapshot.CoreClockMHz} MHz · {snapshot.PowerW:F0} W";
    }

    /// <summary>
    /// Applies a profile's clock/power/voltage knobs AND its fan configuration
    /// (through the fan service, so profile switches never fight the curve loop).
    /// Central path used by the Profiles page, hotkeys, startup, and per-game rules.
    /// When <paramref name="gameContext"/> is true, an Auto-fan profile is read as
    /// "no fan opinion" and leaves the user's current fan setup untouched.
    /// The Fans page UI is kept in sync with whatever was applied.
    /// </summary>
    public ApplyResult ApplyProfileFull(Core.Profiles.TuningProfile profile, bool gameContext = false)
    {
        var gpu = _services.Gpus.Count > 0 ? _services.Gpus[0] : null;
        if (gpu is null)
        {
            return new ApplyResult(false, [KnobResult.Fail("profile", "no GPU")]);
        }

        var result = gpu.Tuner.Apply(profile);

        if (_services.FanControl.TryGetValue(gpu.Index, out var fans))
        {
            switch (profile.FanMode)
            {
                case Core.Profiles.FanMode.Fixed:
                    fans.SetFixed(profile.FixedFanPct);
                    Fans.SyncFromApplied(Core.Profiles.FanMode.Fixed, profile.FixedFanPct, profile.FanCurve);
                    break;
                case Core.Profiles.FanMode.Curve when profile.FanCurve is not null:
                    try
                    {
                        fans.SetCurve(profile.FanCurve);
                        Fans.SyncFromApplied(Core.Profiles.FanMode.Curve, profile.FixedFanPct, profile.FanCurve);
                    }
                    catch (ArgumentException)
                    {
                        // Invalid stored curve: leave fans as they are.
                    }

                    break;
                case Core.Profiles.FanMode.Auto when !gameContext:
                    fans.SetAuto();
                    Fans.SyncFromApplied(Core.Profiles.FanMode.Auto, profile.FixedFanPct, null);
                    break;
                default:
                    // Game context + Auto: the profile has no fan opinion.
                    break;
            }
        }

        Tuning.RefreshFromHardware();
        return result;
    }

    private void OnGameStarted(GameRule rule)
    {
        var gpu = _services.Gpus.Count > 0 ? _services.Gpus[0] : null;
        var profile = _services.Profiles.Load(rule.ProfileName);
        if (gpu is null || profile is null)
        {
            return;
        }

        _preGameState = gpu.Tuner.ReadCurrent();
        _preGameFans = Fans.CurrentConfig;
        var result = ApplyProfileFull(profile, gameContext: true);
        TrayAlert?.Invoke(
            "Afterglow profile applied",
            $"'{rule.ProfileName}' for {rule.ExecutableName}" +
            (result.AllSucceeded ? string.Empty : " (some knobs failed)"));
    }

    private void OnGameExited(GameRule rule)
    {
        var gpu = _services.Gpus.Count > 0 ? _services.Gpus[0] : null;
        if (gpu is null || _preGameState is not { } pre)
        {
            return;
        }

        _preGameState = null;
        var restore = new Core.Profiles.TuningProfile
        {
            Name = "pre-game state",
            CoreOffsetMHz = pre.Core,
            MemOffsetMHz = pre.Mem,
            PowerLimitW = pre.Power > 0 ? pre.Power : null,
            VoltageBoostPct = pre.Boost,
            LockedCoreClockMHz = pre.Lock,
        };
        _ = gpu.Tuner.Apply(restore);

        // Fans back to what the user had before the game (if the game profile changed them).
        if (_preGameFans is { } fans && _services.FanControl.TryGetValue(gpu.Index, out var fanService))
        {
            switch (fans.Mode)
            {
                case Core.Profiles.FanMode.Fixed:
                    fanService.SetFixed(fans.FixedPct);
                    break;
                case Core.Profiles.FanMode.Curve:
                    try
                    {
                        fanService.SetCurve(fans.Curve);
                    }
                    catch (ArgumentException)
                    {
                    }

                    break;
                default:
                    fanService.SetAuto();
                    break;
            }

            Fans.SyncFromApplied(fans.Mode, fans.FixedPct, fans.Mode == Core.Profiles.FanMode.Curve ? fans.Curve : null);
            _preGameFans = null;
        }

        TrayAlert?.Invoke("Afterglow", $"{rule.ExecutableName} exited — previous tuning (including fans) restored.");
        Tuning.RefreshFromHardware();
    }

    private void OnDriverReset(string description)
    {
        if (Core.Stress.StabilityStepper.AnyRunning)
        {
            // The stepper deliberately provokes TDRs and owns its own recovery.
            Core.Diagnostics.Log.Info("TDR during stepper run — watchdog reset suppressed.");
            return;
        }

        if (!_services.Settings.ResetOnDriverCrash)
        {
            return;
        }

        foreach (var gpu in _services.Gpus)
        {
            _ = gpu.Tuner.ResetToDefaults();
        }

        foreach (var fans in _services.FanControl.Values)
        {
            fans.SetAuto();
        }

        ShowCrashBanner = true;
        CrashBannerText =
            "A GPU driver reset (TDR) was detected — Afterglow returned all tuning to driver defaults. " +
            "If you were testing an overclock, it is likely unstable. " + description;
        TrayAlert?.Invoke("GPU driver reset detected", "Tuning was reset to driver defaults for safety.");
        Core.Diagnostics.Log.Warn($"TDR watchdog reset: {description}");
        Tuning.RefreshFromHardware();
    }

    private void CheckAlerts(GpuSnapshot snapshot)
    {
        var settings = _services.Settings;
        if (settings.AlertGpuTempC > 0 && snapshot.GpuTempC is uint temp && temp >= settings.AlertGpuTempC)
        {
            TrayAlert?.Invoke("GPU temperature alert", $"GPU core is at {temp}°C (alert set at {settings.AlertGpuTempC}°C).");
        }

        if (settings.AlertMemJunctionTempC > 0 &&
            snapshot.MemJunctionTempC is double mem && mem >= settings.AlertMemJunctionTempC)
        {
            TrayAlert?.Invoke("Memory temperature alert", $"Memory junction is at {mem:F0}°C (alert set at {settings.AlertMemJunctionTempC}°C).");
        }
    }

    [RelayCommand]
    public void ToggleOverlay()
    {
        if (_overlay is not null)
        {
            _overlay.Close();
            _overlay = null;
            _services.UpdateSettings(s => s with { Overlay = s.Overlay with { Enabled = false } });
            return;
        }

        _overlay = new OverlayWindow(_services, _services.Settings.Overlay);
        _overlay.Show();
        _services.UpdateSettings(s => s with { Overlay = s.Overlay with { Enabled = true } });
    }

    public void EnsureOverlayFromSettings()
    {
        if (_services.Settings.Overlay.Enabled && _overlay is null)
        {
            _overlay = new OverlayWindow(_services, _services.Settings.Overlay);
            _overlay.Show();
        }
    }

    /// <summary>Hotkey: reset everything to driver defaults immediately.</summary>
    public void PanicReset()
    {
        foreach (var gpu in _services.Gpus)
        {
            _ = gpu.Tuner.ResetToDefaults();
        }

        foreach (var fans in _services.FanControl.Values)
        {
            fans.SetAuto();
        }

        TrayAlert?.Invoke("Afterglow", "Panic reset: all tuning returned to driver defaults.");
        Tuning.RefreshFromHardware();
    }

    /// <summary>Hotkey: apply the Nth saved profile (alphabetical).</summary>
    public void ApplyProfileSlot(int slot)
    {
        var profiles = _services.Profiles.LoadAll();
        if (slot < 0 || slot >= profiles.Count)
        {
            return;
        }

        var result = ApplyProfileFull(profiles[slot]);
        TrayAlert?.Invoke(
            "Afterglow profile applied",
            $"'{profiles[slot].Name}' (Ctrl+Alt+{slot + 1})" + (result.AllSucceeded ? string.Empty : " — some knobs failed"));
    }

    /// <summary>
    /// Startup restoration (elevated only): re-activate the saved fan mode, then
    /// apply the configured startup profile if it has been marked stable.
    /// Called by the app after the window exists; skipped when a crash-recovery
    /// banner is pending so the user decides first.
    /// </summary>
    public void RestoreStartupState()
    {
        if (_services.DemoMode || !_services.IsElevated || _services.Gpus.Count == 0 || ShowCrashBanner)
        {
            return;
        }

        var fanSettings = _services.Settings.Fans;
        if (_services.FanControl.TryGetValue(_services.Gpus[0].Index, out var fans))
        {
            switch (fanSettings.Mode)
            {
                case "fixed":
                    fans.SetFixed(fanSettings.FixedDutyPct);
                    Core.Diagnostics.Log.Info($"Startup: restored fixed fan duty {fanSettings.FixedDutyPct}%.");
                    break;
                case "curve" when fanSettings.Curve.Validate() is null:
                    fans.SetCurve(fanSettings.Curve);
                    Core.Diagnostics.Log.Info("Startup: restored fan curve.");
                    break;
                default:
                    break;
            }
        }

        if (_services.Settings.ApplyProfileOnStart is string profileName)
        {
            var profile = _services.Profiles.Load(profileName);
            if (profile is null)
            {
                TrayAlert?.Invoke("Afterglow", $"Startup profile '{profileName}' no longer exists.");
            }
            else if (!profile.MarkedStable)
            {
                TrayAlert?.Invoke(
                    "Startup profile not applied",
                    $"'{profileName}' isn't marked stable yet. Validate it (Stability page), then mark it stable in Profiles.");
            }
            else
            {
                var result = ApplyProfileFull(profile);
                Core.Diagnostics.Log.Info($"Startup profile '{profileName}': {(result.AllSucceeded ? "applied" : "partial")}");
                TrayAlert?.Invoke("Afterglow", $"Startup profile '{profileName}' applied.");
            }
        }
    }

    private void CheckCrashRecovery()
    {
        if (_services.DemoMode || !_services.IsElevated)
        {
            return;
        }

        var state = AppliedStateStore.Load();
        if (state is { CleanShutdown: false })
        {
            ShowCrashBanner = true;
            CrashBannerText =
                $"Afterglow didn't shut down cleanly last time (profile '{state.ProfileName}' was applied " +
                $"{state.AppliedAt:g}). If the system crashed, resetting to driver defaults is recommended.";
        }
    }

    [RelayCommand]
    private void ResetAfterCrash()
    {
        foreach (var gpu in _services.Gpus)
        {
            _ = gpu.Tuner.ResetToDefaults();
        }

        foreach (var fans in _services.FanControl.Values)
        {
            fans.SetAuto();
        }

        ShowCrashBanner = false;
        Tuning.RefreshFromHardware();
    }

    [RelayCommand]
    private void DismissCrashBanner()
    {
        // Keep the applied-state file (it still tracks the clock lock, which can
        // survive at the driver level); just stop treating it as a crash.
        AppliedStateStore.MarkCleanShutdown();
        ShowCrashBanner = false;
    }

    private readonly Core.Services.AutomationEngine _automation = new();

    private void ExecuteAutomation(Core.Services.AutomationEvent fired)
    {
        var rule = fired.Rule;
        string metricLabel = rule.Metric switch
        {
            "gpu" => "GPU temperature",
            "memjunction" => "Memory junction",
            "power" => "Board power",
            _ => rule.Metric,
        };

        string action;
        if (!_services.IsElevated)
        {
            action = "no action taken — Afterglow is running without administrator rights";
        }
        else
        {
            switch (rule.Action)
            {
                case "profile" when rule.ActionProfile is { } name && _services.Profiles.Load(name) is { } profile:
                    _ = ApplyProfileFull(profile);
                    action = $"applied profile '{name}'";
                    break;
                case "fans" when _services.Gpus.Count > 0 &&
                    _services.FanControl.TryGetValue(_services.Gpus[0].Index, out var fans):
                    fans.SetFixed(rule.ActionFanPct);
                    action = $"fans set to fixed {rule.ActionFanPct}%";
                    break;
                case "reset":
                    PanicReset();
                    action = "all tuning reset to driver defaults";
                    break;
                default:
                    action = "no action taken (rule misconfigured)";
                    break;
            }
        }

        _services.Flight?.Marker(
            $"automation metric={rule.Metric} value={fired.Value:F0} action={rule.Action}");
        Core.Diagnostics.Log.Info(
            $"Automation: {metricLabel} {fired.Value:F0} >= {rule.Threshold:F0} for {rule.ForSeconds}s -> {action}");
        TrayAlert?.Invoke(
            "Automation rule fired",
            $"{metricLabel} hit {fired.Value:F0} (threshold {rule.Threshold:F0} for {rule.ForSeconds} s) — {action}.");
    }

    [RelayCommand]
    private void ViewCrashReport()
    {
        ShowForensicsBanner = false;
        NavigateTo("stability");
    }

    [RelayCommand]
    private void DismissForensicsBanner() => ShowForensicsBanner = false;

    [RelayCommand]
    public void NavigateTo(string page)
    {
        CurrentPageName = page.ToLowerInvariant();
        CurrentPage = CurrentPageName switch
        {
            "tuning" => Tuning,
            "fans" => Fans,
            "metrics" => Metrics,
            "vfcurve" => VfCurve,
            "stability" => Stability,
            "profiles" => Profiles,
            "settings" => Settings,
            _ => Dashboard,
        };
    }
}
