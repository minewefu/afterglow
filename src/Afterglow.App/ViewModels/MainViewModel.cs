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

    /// <summary>
    /// The card a game rule's profile landed on, as it was just before — one
    /// object, so its parts can never describe different cards or sessions.
    /// </summary>
    private PreGameSnapshot? _preGame;

    public DashboardViewModel Dashboard { get; }

    public TuningViewModel Tuning { get; }

    public FansViewModel Fans { get; }

    public MetricsViewModel Metrics { get; }

    public VfCurveViewModel VfCurve { get; }

    public StabilityViewModel Stability { get; }

    public ProfilesViewModel Profiles { get; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial ObservableObject CurrentPage { get; set; }

    [ObservableProperty]
    public partial string CurrentPageName { get; set; } = "dashboard";

    [ObservableProperty]
    public partial bool ShowCrashBanner { get; set; }

    [ObservableProperty]
    public partial string CrashBannerText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowForensicsBanner { get; set; }

    [ObservableProperty]
    public partial string ForensicsBannerText { get; set; } = string.Empty;

    [ObservableProperty] public partial string GpuName { get; set; } = string.Empty;

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
        SelectedGpuOption = 0;

        Dashboard = new DashboardViewModel(services);
        Tuning = new TuningViewModel(services);
        Fans = new FansViewModel(services);
        Metrics = new MetricsViewModel(services);
        VfCurve = new VfCurveViewModel(services);
        Stability = new StabilityViewModel(services);
        Profiles = new ProfilesViewModel(services, Tuning, Fans, ApplyFromProfilesPage);
        Settings = new SettingsViewModel(services);
        Tuning.ResetCompleted += Fans.NoteReset;

        CurrentPage = Dashboard;
        services.WindowVisibleChanged += _ => UpdateLivePages();

        CheckCrashRecovery();

        if (services.LastCrashReport is { } crash &&
            DateTimeOffset.Now - crash.CrashedAt < TimeSpan.FromHours(72))
        {
            ShowForensicsBanner = true;
            // Name the card the evidence came from: the offsets and load state in
            // the report belong to exactly one GPU, and on a multi-GPU machine an
            // unlabelled verdict reads as a statement about the whole system.
            string card = crash.GpuName is { } name ? $" [GPU {crash.GpuIndex} — {name}]" : string.Empty;

            // Show the verdict's own headline rather than prefixing a flat
            // assertion onto it. The staleness verdict deliberately says the
            // evidence "cannot be tied to what the GPU was doing when this
            // session ended" — asserting "Last session ended in a crash."
            // directly above that contradicted the classifier one file over, and
            // widening the correlation window made that pairing reachable for
            // sessions that did not crash at all.
            ForensicsBannerText = $"Last session ended without a clean shutdown.{card} {crash.Headline}";
        }

        _automation.UpdateRules(services.Settings.AutomationRules);
        services.SettingsChanged += updated =>
        {
            _automation.UpdateRules(updated.AutomationRules);

            // Overlay options apply to an overlay that is already open (F-73).
            _overlay?.ApplySettings(updated.Overlay);
        };
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
        services.GameWatcher.GameHandedOff += (exited, next) =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnGameHandedOff(exited, next));
        services.GameWatcher.GameExited += rule =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnGameExited(rule));
        services.TdrWatchdog.DriverResetDetected += description =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnDriverReset(description));
        // Decided on the telemetry thread (no dispatcher hop on a quiet tick);
        // only a new crossing is marshalled, because the alert ends in a
        // WinForms NotifyIcon balloon, which is not thread-safe.
        services.Telemetry.SnapshotTaken += snapshot =>
        {
            var settings = _services.Settings;
            var raised = _temperatureAlerts.Evaluate(snapshot, settings.AlertGpuTempC, settings.AlertMemJunctionTempC);
            if (raised.Count > 0)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => ShowAlerts(raised));
            }
        };

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
                        $"Afterglow {result.LatestTag} is out (you're on v{Core.Services.UpdateChecker.DisplayVersion}). " +
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

    [ObservableProperty] public partial int SelectedGpuOption { get; set; }

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

        // The fan configuration is recorded for the card it landed on: the
        // Fans page editor for the selected card, that card's own saved entry
        // for any other — the editor mirrors the SELECTED card only, and an
        // apply landing on another GPU used to persist nothing at all (R-23).
        if (_services.FanControl.TryGetValue(gpu.Index, out var fans))
        {
            switch (profile.FanMode)
            {
                case Core.Profiles.FanMode.Fixed:
                    results.Add(fans.SetFixed(profile.FixedFanPct)
                        ? KnobResult.Ok("fans", $"fixed {profile.FixedFanPct}%")
                        : KnobResult.Fail("fans", $"the driver did not accept fixed {profile.FixedFanPct}%"));
                    Fans.RecordApplied(gpu, Core.Profiles.FanMode.Fixed, profile.FixedFanPct, profile.FanCurve);
                    break;
                case Core.Profiles.FanMode.Curve when profile.FanCurve is not null:
                    try
                    {
                        fans.SetCurve(profile.FanCurve);
                        results.Add(KnobResult.Ok("fans", "curve armed"));
                        Fans.RecordApplied(gpu, Core.Profiles.FanMode.Curve, profile.FixedFanPct, profile.FanCurve);
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
                    Fans.RecordApplied(gpu, Core.Profiles.FanMode.Auto, profile.FixedFanPct, null);
                    break;
                default:
                    // Game context + Auto: the profile has no fan opinion.
                    break;
            }
        }

        // The Tuning sliders show the selected card. Reloading them after an
        // apply to ANOTHER GPU changed nothing they show, and threw away the
        // edit the user was composing there (R-49).
        if (gpu.Index == _services.SelectedGpu?.Index)
        {
            Tuning.RefreshFromHardware();
        }

        return results.Count == result.Results.Count
            ? result
            : new ApplyResult(results.All(r => r.Applied), results);
    }

    /// <summary>
    /// The Profiles page's Apply. The profile is loaded into the Tuning sliders
    /// first only when it lands on the SELECTED card: a full apply reloads them
    /// when it lands, so they end on what the card is running, and a refused
    /// apply leaves the profile loaded and visibly unapplied. A profile stamped
    /// for another GPU leaves the selected card's sliders — and any edit in
    /// progress there — alone; loading it put another card's values on them
    /// (R-49).
    /// </summary>
    private ApplyResult ApplyFromProfilesPage(Core.Profiles.TuningProfile profile)
    {
        var gpu = TargetGpuFor(profile);
        if (gpu is null || gpu.Index == _services.SelectedGpu?.Index)
        {
            Tuning.LoadProfile(profile);
        }

        return ApplyProfileFull(profile);
    }

    private void OnGameStarted(GameRule rule)
    {
        // A snapshot from an earlier session (a rule without revert-on-exit)
        // must not survive into this one, whatever happens below (F-16).
        _preGame = null;

        var profile = _services.Profiles.Load(rule.ProfileName);
        var gpu = profile is null ? null : TargetGpuFor(profile);
        if (gpu is null || profile is null)
        {
            return;
        }

        // The apply below is pinned to this card, which makes the identity
        // check ours: a profile stamped for a card that isn't here moves nothing.
        if (!profile.AppliesToGpu(gpu.Tuner.GpuUuid))
        {
            TrayAlert?.Invoke(
                "Afterglow profile not applied",
                $"'{rule.ProfileName}' for {rule.ExecutableName} was saved for a different GPU " +
                $"({profile.GpuName ?? profile.GpuUuid}) — re-save it on this card to use it here.");
            return;
        }

        // Read the card the profile lands on — its fans from its own fan
        // service and settings, not from the Fans page editor, which shows the
        // SELECTED card (F-16); its temperature limit and V/F table, which
        // ReadCurrent cannot see (F-21).
        FanRestoreState? fans = _services.FanControl.TryGetValue(gpu.Index, out var fanService)
            ? GameSessionRestore.CaptureFans(fanService.Mode, fanService.CommandedDuty, _services.FanSettingsFor(gpu))
            : null;
        var snapshot = GameSessionRestore.Capture(gpu.Tuner, gpu.Index, rule.ExecutableName, profile, fans);

        var result = ApplyProfileFull(profile, gameContext: true, target: gpu);

        // Refused outright (invalid, or stamped for a card that isn't here):
        // nothing moved, so there is nothing to put back on exit.
        _preGame = result.Results.Any(r => r.Knob == "profile" && !r.Applied) ? null : snapshot;
        TrayAlert?.Invoke(
            "Afterglow profile applied",
            $"'{rule.ProfileName}' for {rule.ExecutableName}" +
            (result.AllSucceeded ? string.Empty : " (some knobs failed)"));
    }

    /// <summary>
    /// The game whose profile was active exited while <paramref name="next"/>,
    /// another configured game, still runs (R1). Its profile used to stay on
    /// until the last game closed. Now <paramref name="next"/>'s profile takes
    /// over, and the snapshot taken before the first game stays the restore
    /// target; see <see cref="GameHandoff"/> for the three ways this goes.
    /// </summary>
    private void OnGameHandedOff(GameRule exited, GameRule next)
    {
        var pre = _preGame;
        var profile = _services.Profiles.Load(next.ProfileName);
        var gpu = profile is null ? null : TargetGpuFor(profile);
        switch (GameSessionRestore.PlanHandoff(pre, profile, gpu?.Index, gpu?.Tuner.GpuUuid))
        {
            case GameHandoff.Swap when pre is not null && profile is not null && gpu is not null:
                SwapGameProfile(pre, exited, next, profile, gpu);
                break;
            case GameHandoff.EndThenStart:
                // The exited game's part ends as its own exit would — restored
                // if its rule says so, otherwise left as it is with the snapshot
                // dropped — and the remaining game starts as if alone.
                if (exited.RevertOnExit)
                {
                    OnGameExited(exited);
                }
                else
                {
                    _preGame = null;
                }

                OnGameStarted(next);
                break;
            default:
                OnGameStarted(next);
                break;
        }
    }

    /// <summary>
    /// A same-card hand-off: the pre-game state goes back first, without being
    /// consumed, then the next game's profile is applied on top — exactly what
    /// that game gets when it starts alone, and nothing of the exited game's
    /// profile survives where the next one has no value of its own. No new
    /// snapshot is captured: one taken now would make the exited game's
    /// leftovers "the state before the game".
    /// </summary>
    private void SwapGameProfile(
        PreGameSnapshot pre, GameRule exited, GameRule next, Core.Profiles.TuningProfile profile,
        Core.Hardware.GpuContext gpu)
    {
        var (restore, fansRestored, plan) = RestorePreGame(pre, gpu);
        var applied = ApplyProfileFull(profile, gameContext: true, target: gpu);
        _preGame = GameSessionRestore.HandedOff(pre, next.ExecutableName, profile);

        string message = GameSessionRestore.DescribeHandoff(
            exited.ExecutableName, next, restore, fansRestored, plan.NotRestored, applied);
        Core.Diagnostics.Log.Info(
            $"Game hand-off on GPU {gpu.Index}: {message} Restore: {restore.Summary} Apply: {applied.Summary}");
        TrayAlert?.Invoke("Afterglow profile applied", message);

        // A refused apply returns before ApplyProfileFull refreshes the sliders,
        // and the restore half did change the card.
        if (applied.Results.Any(r => r.Knob == "profile" && !r.Applied) && gpu.Index == _services.SelectedGpu?.Index)
        {
            Tuning.RefreshFromHardware();
        }
    }

    /// <summary>
    /// Puts a session's pre-game state back on its card: the tuning, and the
    /// fans when the game profile changed them. Does not consume the snapshot.
    /// </summary>
    private (ApplyResult Result, bool? FansRestored, GameRestorePlan Plan) RestorePreGame(
        PreGameSnapshot pre, Core.Hardware.GpuContext gpu)
    {
        var plan = GameSessionRestore.Plan(pre);

        // The plan carries the V/F table itself when the game could have
        // changed it; otherwise it has no opinion on the curve, and must not be
        // read as "the user wants no curve".
        var result = gpu.Tuner.Apply(plan.Profile, reconcileVfPoints: false);

        // Fans only when the game profile changed them, and only this card's.
        bool? fansRestored = null;
        if (plan.Fans is { } fans && _services.FanControl.TryGetValue(gpu.Index, out var fanService))
        {
            fansRestored = GameSessionRestore.ApplyFans(fanService, fans);
            if (fansRestored == true)
            {
                // Persisted for this card whichever card is selected (R-23):
                // otherwise its saved fans kept describing the game's.
                Fans.RecordApplied(gpu, fans.Mode, fans.FixedPct, fans.Curve);
            }
        }

        return (result, fansRestored, plan);
    }

    private void OnGameExited(GameRule rule)
    {
        // Consumed exactly once: a session's snapshot is never applied twice,
        // nor after a later session.
        var pre = _preGame;
        _preGame = null;
        if (pre is null)
        {
            return;
        }

        // Back onto the card the snapshot was read from — by identity, never
        // whichever card the title bar shows now.
        var gpu = _services.Gpus.FirstOrDefault(g =>
            g.Index == pre.GpuIndex &&
            (pre.GpuUuid is null || string.Equals(g.Tuner.GpuUuid, pre.GpuUuid, StringComparison.OrdinalIgnoreCase)));
        if (gpu is null)
        {
            Core.Diagnostics.Log.Warn($"Game exit restore skipped: GPU {pre.GpuIndex} is no longer the card it was read from.");
            return;
        }

        var (result, fansRestored, plan) = RestorePreGame(pre, gpu);
        string message = GameSessionRestore.Describe(rule.ExecutableName, result, fansRestored, plan.NotRestored);
        Core.Diagnostics.Log.Info($"Game exit restore on GPU {gpu.Index}: {message} {result.Summary}");
        TrayAlert?.Invoke("Afterglow", message);

        // Only the card the restore landed on; see ApplyProfileFull (R-49).
        if (gpu.Index == _services.SelectedGpu?.Index)
        {
            Tuning.RefreshFromHardware();
        }
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

        // Right after a driver fault is exactly when writes get refused, so the
        // banner reports what the reset did, not what it asked for (R-10).
        var cards = ResetAllCards();
        bool nameCards = _services.Gpus.Count > 1;
        ShowCrashBanner = true;
        CrashBannerText = DefaultsReset.DriverResetBanner(cards, nameCards, description);
        TrayAlert?.Invoke("GPU driver reset detected", DefaultsReset.DriverResetTray(cards, nameCards));
        Core.Diagnostics.Log.Warn($"TDR watchdog reset: {description}");
        Tuning.RefreshFromHardware();
        NoteFanResets(cards);
    }

    /// <summary>
    /// Every card back to driver defaults — its fan service first, then its
    /// tuner (<see cref="DefaultsReset.Card"/>) — keeping each card's outcome;
    /// the panic and driver-crash resets used to discard them and announce
    /// success whatever the driver answered (R-10). An incomplete card's
    /// per-knob answers go to the log, where the short messages point.
    /// </summary>
    private List<CardResetResult> ResetAllCards()
    {
        var cards = new List<CardResetResult>(_services.Gpus.Count);
        foreach (var gpu in _services.Gpus)
        {
            _services.FanControl.TryGetValue(gpu.Index, out var fans);
            var card = DefaultsReset.Card(gpu.Index, gpu.Tuner, fans);
            if (!card.AllReturned)
            {
                Core.Diagnostics.Log.Warn(
                    $"Reset to driver defaults incomplete on GPU {gpu.Index}: {card.AsApplyResult().Summary}");
            }

            cards.Add(card);
        }

        return cards;
    }

    /// <summary>Tells the Fans page what a reset did to the selected card's fans (UI thread).</summary>
    private void NoteFanResets(IReadOnlyList<CardResetResult> cards)
    {
        foreach (var card in cards)
        {
            Fans.NoteReset(card);
        }
    }

    private readonly Core.Services.TemperatureAlertMonitor _temperatureAlerts = new();

    private void ShowAlerts(IReadOnlyList<Core.Services.TemperatureAlert> raised)
    {
        foreach (var alert in raised)
        {
            // The tray throttles by title, so on a multi-GPU machine each card
            // needs its own title — otherwise the second card's crossing inside
            // the same minute is dropped, and (edge-triggered) never raised again.
            bool core = alert.Sensor == Core.Services.AlertSensor.Gpu;
            string gpu = _services.Gpus.Count > 1 ? $"GPU {alert.DeviceIndex}" : "GPU";
            string title = $"{gpu} {(core ? "temperature" : "memory temperature")} alert";
            string message = $"{gpu} {(core ? "core" : "memory junction")} reached {alert.ValueC:F0}°C " +
                             $"(alert set at {alert.ThresholdC}°C).";
            Core.Diagnostics.Log.Info($"{title}: {message}");
            TrayAlert?.Invoke(title, message);
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

    /// <summary>
    /// Hotkey: reset everything to driver defaults immediately.
    /// <para>
    /// Anything actively driving the GPU is stopped FIRST. A stepper mid-step
    /// does not notice a reset: it keeps burning, and the rest of that step runs
    /// at stock clocks, so the offset the reset just removed is scored as having
    /// passed — a stability verdict from evidence gathered while the setting was
    /// not applied, later published as the stable offset and handed to MCP
    /// agents. Its next iteration then re-writes the very offset that was reset.
    /// A V/F probe likewise re-pins the clock the reset just released.
    /// <see cref="OnDriverReset"/> already refused to fight a running stepper;
    /// this path did not.
    /// </para>
    /// </summary>
    public void PanicReset() => _ = StartPanicReset();

    /// <summary>
    /// The panic reset (see <see cref="PanicReset"/>). Returns each card's
    /// outcome when the reset ran at once; null when a run was still stopping
    /// and the reset finishes off the message pump. Either way it reports
    /// itself through <see cref="TrayAlert"/>.
    /// </summary>
    private List<CardResetResult>? StartPanicReset()
    {
        // Asking is instant, so it happens on the calling thread: a run stops
        // advancing the moment the key is pressed.
        bool wasActive = Stability.AnyRunActive || VfCurve.ProbeRunning;
        Stability.RequestStop();
        VfCurve.RequestProbeStop();

        if (!wasActive)
        {
            return CompletePanicReset(stopped: true);
        }

        // Something is running, and unwinding it can take seconds — the stepper's
        // offset restore alone retries three times over ~4.5 s. This method is
        // invoked from the window's message pump (HotkeyService.WndProc), so
        // waiting here would freeze the UI and the hotkey handler itself. Wait
        // off the pump instead and report the real outcome when it is known.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var budget = TimeSpan.FromSeconds(8);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool stopped = Stability.WaitForStop(budget);
            var left = budget - clock.Elapsed;
            stopped &= VfCurve.CancelProbeAndWait(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            _ = CompletePanicReset(stopped);
        });
        return null;
    }

    private List<CardResetResult> CompletePanicReset(bool stopped)
    {
        // The message is derived from what every card answered (R-10): it used
        // to say "all tuning returned to driver defaults" over a refused write.
        var cards = ResetAllCards();
        string message = DefaultsReset.PanicMessage(cards, stopped, _services.Gpus.Count > 1);

        void Finish()
        {
            TrayAlert?.Invoke("Afterglow", message);
            Tuning.RefreshFromHardware();
            NoteFanResets(cards);
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Finish);
        }
        else
        {
            Finish();
        }

        return cards;
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
        string shortcut = _services.Hotkeys.StatusOf(Core.Hotkeys.HotkeyActions.ProfileSlot(slot))?.Chord is { } chord
            ? $" ({chord})"
            : string.Empty;
        TrayAlert?.Invoke(
            "Afterglow profile applied",
            $"'{profiles[slot].Name}'{shortcut}" + (result.AllSucceeded ? string.Empty : " — some knobs failed"));
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
        // (reset-after-crash already resets every GPU). A probe pin is the
        // graver of the two stories and is announced first — whichever record
        // enumerated first used to win, so a second card's pin was shown as an
        // ordinary unclean exit and dismissed with it.
        var uncleanAll = AppliedStateStore.LoadAll().Where(s => !s.CleanShutdown || s.ProbeLockPending).ToList();
        var unclean = uncleanAll.FirstOrDefault(s => s.ProbeLockPending) ?? uncleanAll.FirstOrDefault();
        if (unclean is not null)
        {
            ShowCrashBanner = true;
            string others = uncleanAll.Count > 1 ? $" {uncleanAll.Count} GPU records are affected." : string.Empty;
            CrashBannerText = (unclean.ProbeLockPending
                ? $"A V/F probe could not release its clock lock ({unclean.AppliedAt:g}); the GPU may still be " +
                  "pinned at an exact frequency. Resetting to driver defaults is recommended."
                : $"Afterglow didn't shut down cleanly last time (profile '{unclean.ProfileName}' was applied " +
                  $"{unclean.AppliedAt:g}). If the system crashed, resetting to driver defaults is recommended.")
                + others;
        }
    }

    [RelayCommand]
    private void ResetAfterCrash()
    {
        // Fan service first, then tuner, per card — the same order as every
        // other reset, so a curve armed meanwhile can't re-pin the fans between
        // the two releases (R-09).
        var cards = ResetAllCards();

        // No sweep of probe-lock records here: each tuner resolves its own
        // card's record (by the stable key the GPU manager gave it) when its
        // clock-lock reset verifiably lands — and only then. Resolving every
        // index-keyed record unconditionally erased the only trace of a pin on
        // a card whose reset the driver had just refused, and a record for a
        // card that is absent right now is the only trace of a lock nobody
        // released.

        // Re-read the store rather than assume: a record for a card that is
        // absent, or whose reset the driver refused, is still there, and
        // hiding the banner over it left the startup profile suppressed on
        // every launch with nothing on screen saying why.
        var remaining = AppliedStateStore.LoadAll().Where(s => !s.CleanShutdown || s.ProbeLockPending).ToList();
        if (remaining.Count == 0)
        {
            ShowCrashBanner = false;
        }
        else
        {
            CrashBannerText =
                $"Reset done, but {remaining.Count} applied-state record(s) could not be resolved by it — a card " +
                "that is not present right now, or a driver that refused its reset. If you know the card is free, " +
                "Dismiss clears them.";
        }

        Tuning.RefreshFromHardware();
        NoteFanResets(cards);
    }

    [RelayCommand]
    private void DismissCrashBanner()
    {
        // Keep the applied-state file (it still tracks the clock lock, which can
        // survive at the driver level); just stop treating it as a crash. The
        // user is explicitly waving the warning off, so a probe's unresolved
        // pin is dismissed with it.
        AppliedStateStore.MarkCleanShutdown(resolveProbeLocks: true);
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
                    // What the reset did, not what it asked for (R-10).
                    action = StartPanicReset() is { } cards
                        ? DefaultsReset.AutomationAction(cards, _services.Gpus.Count > 1)
                        : "stopping the running stability task, then resetting all tuning to driver defaults";
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

    partial void OnCurrentPageChanged(ObservableObject value) => UpdateLivePages();

    /// <summary>
    /// Exactly one page is live: the one on screen, and only while the window
    /// is. Everything else skips its per-tick display work (see ILivePage).
    /// </summary>
    private void UpdateLivePages()
    {
        bool visible = _services.WindowVisible;
        ObservableObject[] pages = [Dashboard, Tuning, Fans, Metrics, VfCurve, Stability, Profiles, Settings];

        // Off before on, so a switch never has two pages drawing at once.
        foreach (var page in pages)
        {
            if (page is ILivePage live && !(visible && ReferenceEquals(page, CurrentPage)))
            {
                live.IsLive = false;
            }
        }

        if (visible && CurrentPage is ILivePage current)
        {
            current.IsLive = true;
        }
    }

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
