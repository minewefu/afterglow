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
    private (int Core, int Mem, double? Power, uint? Boost, uint? Lock)? _preGameState;
    private (Core.Profiles.FanMode Mode, uint FixedPct, Core.Fans.FanCurveConfig Curve)? _preGameFans;
    private Core.Hardware.GpuContext? _preGameGpu;

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

    [ObservableProperty] private string _gpuName = string.Empty;

    public string DriverText => _services.DemoMode
        ? "Synthetic demo data"
        : _services.SelectedGpu is { } gpu
            ? $"{(gpu.Vendor == Core.Hardware.GpuVendor.Intel ? "Intel" : "NVIDIA")} driver {gpu.DriverVersion ?? "—"}"
            : "GPU driver —";

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
            : services.SelectedGpu?.Name ?? "No supported GPU detected";

        GpuOptions = services.Gpus.Select(g => $"GPU {g.Index} — {g.Name}").ToArray();
        _selectedGpuOption = 0;

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
            // Rules watch every GPU; the engine tracks breaches per card.
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
        // Alerts end in a WinForms NotifyIcon balloon, which is not
        // thread-safe — marshal off the telemetry polling thread like every
        // other cross-thread handler here.
        services.Telemetry.SnapshotTaken += snapshot =>
            Application.Current?.Dispatcher.BeginInvoke(() => CheckAlerts(snapshot));

        // A failing fan command (lost elevation, driver refusal) must reach
        // the user, not just the log.
        foreach (var fans in services.FanControl.Values)
        {
            fans.CommandFailed += rc =>
                Application.Current?.Dispatcher.BeginInvoke(() =>
                    TrayAlert?.Invoke(
                        "Fan command failed",
                        rc == Core.Interop.Nvml.NvmlReturn.NotSupported
                            ? "This GPU does not expose fan control through the driver, so the fan command did nothing."
                            : $"The driver refused a fan command ({rc}). Fan control may need administrator rights."));
        }

        if (services.Settings.UpdateCheckEnabled && !services.DemoMode)
        {
            StartupUpdateCheck();
        }
    }

    /// <summary>
    /// Fire-and-forget opt-in update check: waits out the startup burst, makes
    /// one request, and only ever surfaces a tray balloon when something newer
    /// exists. Every failure path is silent by design — an update check must
    /// never produce an error the user has to deal with.
    /// </summary>
    private void StartupUpdateCheck() => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var current = Core.Services.UpdateChecker.CurrentVersion ?? new Version(0, 0, 0);
            var result = await Core.Services.UpdateChecker.CheckAsync(current).ConfigureAwait(false);
            if (result is { UpdateAvailable: true })
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                    TrayAlert?.Invoke(
                        "Update available",
                        $"Afterglow {result.LatestTag} is out (you're on v{current.ToString(3)}). " +
                        "Settings → About has the Releases page."));
            }
        }
        catch (Exception)
        {
        }
    });

    /// <summary>Raised for tray balloon alerts (title, message).</summary>
    public event Action<string, string>? TrayAlert;

    /// <summary>"GPU 0 — name" entries for the title-bar selector.</summary>
    public IReadOnlyList<string> GpuOptions { get; }

    public bool HasMultipleGpus => _services.Gpus.Count > 1;

    [ObservableProperty] private int _selectedGpuOption;

    partial void OnSelectedGpuOptionChanged(int value)
    {
        if (value < 0 || value >= _services.Gpus.Count)
        {
            return;
        }

        _services.SelectGpu(_services.Gpus[value].Index);
        GpuName = _services.SelectedGpu?.Name ?? GpuName;
        OnPropertyChanged(nameof(DriverText));

        // Every page follows the selection; runs already in flight keep the
        // card they started on.
        Dashboard.RebindGpu();
        Tuning.RebindGpu();
        Fans.RebindGpu();
        VfCurve.RebindGpu();
        Stability.RebindGpu();
    }

    /// <summary>Live one-line status for the tray tooltip.</summary>
    public string BuildTrayTooltip()
    {
        var snapshot = _services.SelectedGpu is { } gpu
            ? _services.Telemetry.HistoryFor(gpu.Index).Latest
            : _services.DemoMode ? _services.Telemetry.HistoryFor(0).Latest : null;
        return snapshot is null
            ? "Afterglow"
            : $"Afterglow — {snapshot.GpuTempC}°C · {snapshot.CoreClockMHz} MHz · {snapshot.PowerW:F0} W";
    }

    /// <summary>
    /// The GPU a profile should land on: the card it was stamped with when
    /// present, else the selected card. A stamped profile whose card is absent
    /// still resolves to the selected GPU so the tuner's identity gate can
    /// refuse it with the honest message instead of silently doing nothing.
    /// </summary>
    private Core.Hardware.GpuContext? TargetGpuFor(Core.Profiles.TuningProfile profile) =>
        profile.GpuUuid is { } uuid
            ? _services.Gpus.FirstOrDefault(g =>
                  string.Equals(g.Uuid, uuid, StringComparison.OrdinalIgnoreCase)) ?? _services.SelectedGpu
            : _services.SelectedGpu;

    /// <summary>
    /// Applies a profile's clock/power/voltage knobs AND its fan configuration
    /// (through the fan service, so profile switches never fight the curve loop).
    /// Central path used by the Profiles page, hotkeys, startup, and per-game rules.
    /// When <paramref name="gameContext"/> is true, an Auto-fan profile is read as
    /// "no fan opinion" and leaves the user's current fan setup untouched.
    /// <paramref name="target"/> pins the apply to one specific card — automation
    /// aims at the card that breached, which is not necessarily the stamped or the
    /// selected one; when it is null the profile's own stamp, else the selected
    /// card, decides as before. A caller that passes a target owns the identity
    /// check (TuningProfile.AppliesToGpu) — the fan half of an apply runs whether
    /// or not the clock half was refused.
    /// The Fans page UI is kept in sync with whatever was applied.
    /// </summary>
    public ApplyResult ApplyProfileFull(
        Core.Profiles.TuningProfile profile,
        bool gameContext = false,
        Core.Hardware.GpuContext? target = null)
    {
        var gpu = target ?? TargetGpuFor(profile);
        if (gpu is null)
        {
            return new ApplyResult(false, [KnobResult.Fail("profile", "no GPU")]);
        }

        var result = gpu.Tuner.Apply(profile);

        // A profile the engine refused outright — invalid, or stamped for
        // another card — never reached a knob, so the fan half must not run
        // either: applying one card's fan curve to another, or a duty from a
        // profile that failed validation, is exactly what the refusal prevents.
        if (result.Results.Any(r => r.Knob == "profile" && !r.Applied))
        {
            return result;
        }

        // The fan half is part of the apply, so its outcome is part of the
        // result: a profile whose fan command the driver refused is a partial
        // apply, and every caller that reports to the user reads AllSucceeded.
        var results = result.Results.ToList();

        // The Fans page editor mirrors the SELECTED card only — an apply
        // landing on another GPU must not rewrite the editor's state.
        bool syncEditor = gpu.Index == _services.SelectedGpu?.Index;
        if (_services.FanControl.TryGetValue(gpu.Index, out var fans))
        {
            switch (profile.FanMode)
            {
                case Core.Profiles.FanMode.Fixed:
                    results.Add(fans.SetFixed(profile.FixedFanPct)
                        ? KnobResult.Ok("fans", $"fixed {profile.FixedFanPct}%")
                        : KnobResult.Fail("fans", $"the driver did not accept fixed {profile.FixedFanPct}%"));
                    if (syncEditor)
                    {
                        Fans.SyncFromApplied(Core.Profiles.FanMode.Fixed, profile.FixedFanPct, profile.FanCurve);
                    }

                    break;
                case Core.Profiles.FanMode.Curve when profile.FanCurve is not null:
                    try
                    {
                        fans.SetCurve(profile.FanCurve);
                        results.Add(KnobResult.Ok("fans", "curve armed"));
                        if (syncEditor)
                        {
                            Fans.SyncFromApplied(Core.Profiles.FanMode.Curve, profile.FixedFanPct, profile.FanCurve);
                        }
                    }
                    catch (ArgumentException ex)
                    {
                        // Invalid stored curve: leave fans as they are — and say so.
                        results.Add(KnobResult.Fail("fans", $"stored curve rejected: {ex.Message}"));
                    }

                    break;
                case Core.Profiles.FanMode.Curve:
                    results.Add(KnobResult.Fail("fans", "the profile selects a fan curve but carries none"));
                    break;
                case Core.Profiles.FanMode.Auto when !gameContext:
                    results.Add(fans.SetAuto()
                        ? KnobResult.Ok("fans", "firmware (auto)")
                        : KnobResult.Fail("fans", "the driver did not accept the release to firmware control"));
                    if (syncEditor)
                    {
                        Fans.SyncFromApplied(Core.Profiles.FanMode.Auto, profile.FixedFanPct, null);
                    }

                    break;
                default:
                    // Game context + Auto: the profile has no fan opinion.
                    break;
            }
        }

        Tuning.RefreshFromHardware();
        return results.Count == result.Results.Count
            ? result
            : new ApplyResult(results.All(r => r.Applied), results);
    }

    private void OnGameStarted(GameRule rule)
    {
        var profile = _services.Profiles.Load(rule.ProfileName);
        var gpu = profile is null ? null : TargetGpuFor(profile);
        if (gpu is null || profile is null)
        {
            return;
        }

        _preGameGpu = gpu;
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
        // Restore to the exact card the game profile landed on.
        var gpu = _preGameGpu ?? _services.SelectedGpu;
        if (gpu is null || _preGameState is not { } pre)
        {
            return;
        }

        _preGameState = null;
        _preGameGpu = null;
        var restore = new Core.Profiles.TuningProfile
        {
            Name = "pre-game state",
            CoreOffsetMHz = pre.Core,
            MemOffsetMHz = pre.Mem,
            PowerLimitW = pre.Power is > 0 ? pre.Power : null,
            VoltageBoostPct = pre.Boost,
            LockedCoreClockMHz = pre.Lock,
        };
        // Built from ReadCurrent, which cannot see per-point offsets, so this
        // restore must not be read as "the user wants no curve".
        _ = gpu.Tuner.Apply(restore, reconcileVfPoints: false);

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

            if (gpu.Index == _services.SelectedGpu?.Index)
            {
                Fans.SyncFromApplied(fans.Mode, fans.FixedPct, fans.Mode == Core.Profiles.FanMode.Curve ? fans.Curve : null);
            }

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

        // Every card restores its own persisted fan configuration.
        foreach (var restoreGpu in _services.Gpus)
        {
            if (!_services.FanControl.TryGetValue(restoreGpu.Index, out var fans))
            {
                continue;
            }

            var fanSettings = _services.FanSettingsFor(restoreGpu);
            switch (fanSettings.Mode)
            {
                case "fixed":
                    Core.Diagnostics.Log.Info(fans.SetFixed(fanSettings.FixedDutyPct)
                        ? $"Startup: restored fixed fan duty {fanSettings.FixedDutyPct}% (GPU {restoreGpu.Index})."
                        : $"Startup: the driver did not accept fixed fan duty {fanSettings.FixedDutyPct}% " +
                          $"(GPU {restoreGpu.Index}); the fans are unchanged.");
                    break;
                case "curve" when fanSettings.Curve.Validate() is null:
                    fans.SetCurve(fanSettings.Curve);
                    Core.Diagnostics.Log.Info($"Startup: restored fan curve (GPU {restoreGpu.Index}).");
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

        // Per-GPU records: any card with an unclean record raises the banner
        // (reset-after-crash already resets every GPU).
        var unclean = AppliedStateStore.LoadAll().FirstOrDefault(s => !s.CleanShutdown);
        if (unclean is not null)
        {
            ShowCrashBanner = true;
            CrashBannerText =
                $"Afterglow didn't shut down cleanly last time (profile '{unclean.ProfileName}' was applied " +
                $"{unclean.AppliedAt:g}). If the system crashed, resetting to driver defaults is recommended.";
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
                    // Throttle the card that actually breached — not whatever card
                    // the profile is stamped for or the title bar happens to show.
                    action = ApplyAutomationProfile(name, profile, fired.DeviceIndex);
                    break;
                case "fans" when _services.FanControl.TryGetValue(fired.DeviceIndex, out var fans):
                    // Pin the fans on the card that actually breached — and report
                    // the driver's answer, not the request.
                    action = fans.SetFixed(rule.ActionFanPct)
                        ? $"fans set to fixed {rule.ActionFanPct}%"
                        : $"the driver did not accept fixed {rule.ActionFanPct}% on the fans";
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

        string gpuLabel = _services.Gpus.Count > 1
            ? $" on GPU {fired.DeviceIndex}"
            : string.Empty;
        _services.Flight?.Marker(
            $"automation metric={rule.Metric} gpu={fired.DeviceIndex} value={fired.Value:F0} action={rule.Action}");
        Core.Diagnostics.Log.Info(
            $"Automation: {metricLabel}{gpuLabel} {fired.Value:F0} >= {rule.Threshold:F0} for {rule.ForSeconds}s -> {action}");
        TrayAlert?.Invoke(
            "Automation rule fired",
            $"{metricLabel}{gpuLabel} hit {fired.Value:F0} (threshold {rule.Threshold:F0} for {rule.ForSeconds} s) — {action}.");
    }

    /// <summary>
    /// The automation "profile" action. A rule watches every card but a profile
    /// carries at most one card's stamp, so there is no configuration that suits
    /// both cards: apply to the card that breached, and refuse a profile stamped
    /// for a different card outright — before any knob, clocks OR fans, moves —
    /// rather than landing it on the wrong GPU. The returned string is what the
    /// log line and the tray balloon report. It is derived from the apply's own
    /// per-knob results — which include the fan half — so a refusal, an invalid
    /// profile and a partial apply are each reported as what they are.
    /// </summary>
    private string ApplyAutomationProfile(string name, Core.Profiles.TuningProfile profile, uint deviceIndex)
    {
        var gpu = _services.Gpus.FirstOrDefault(g => g.Index == deviceIndex);
        if (gpu is null)
        {
            return $"no action taken — GPU {deviceIndex} is not available";
        }

        if (!profile.AppliesToGpu(gpu.Uuid))
        {
            return $"no action taken — profile '{name}' is saved for {profile.GpuName ?? "another card"}, not for GPU {deviceIndex} which is the card that breached; save a profile on that card and point the rule at it";
        }

        // gameContext: an Auto-fan profile means "no fan opinion" here. Without
        // it, the throttle profile would hand the fans of a card that is
        // breaching its thermal limit straight back to firmware.
        var result = ApplyProfileFull(profile, gameContext: true, target: gpu);
        if (result.AllSucceeded)
        {
            return $"applied profile '{name}'";
        }

        // A profile the engine refused never reached a knob, fans included;
        // saying "some knobs failed" would imply a partial apply that never
        // happened.
        var refused = result.Results.FirstOrDefault(r => r.Knob == "profile" && !r.Applied);
        return refused is not null
            ? $"no action taken — profile '{name}' was refused: {refused.Detail}"
            : $"applied profile '{name}' — {result.Summary}";
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
