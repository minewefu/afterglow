using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core.Hardware;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.App.ViewModels;

public partial class VfCurveViewModel : ObservableObject, ILivePage
{
    private readonly AppServices _services;
    private GpuContext? _gpu;

    /// <summary>
    /// The SELECTED card's curve recorder — it moves with the title-bar
    /// selector, so anything long-running (the probe) must capture its recorder
    /// once at start instead of reading this on every tick.
    /// </summary>
    private Core.Tuning.VfCurveRecorder Recorder =>
        _gpu is { } gpu ? _services.VfCurveFor(gpu.Index) : _services.VfCurve;

    [ObservableProperty] public partial IReadOnlyList<VfBin>? Curve { get; set; }
    [ObservableProperty] public partial long PeakSamples { get; set; }
    [ObservableProperty] public partial double LiveVoltage { get; set; }
    [ObservableProperty] public partial double LiveClock { get; set; }
    [ObservableProperty] public partial double TargetVoltage { get; set; }
    [ObservableProperty] public partial double TargetClock { get; set; }
    [ObservableProperty] public partial string SampleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlanText { get; set; } =
        "Click a point on the chart to target it. Afterglow works out the core offset and clock lock that " +
        "hold that clock at that voltage; for an undervolt, pick a point above and to the left of the blue line.";
    [ObservableProperty] public partial bool HasPlan { get; set; }
    [ObservableProperty] public partial string ApplyResultText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool LastApplyFailed { get; set; }

    private UndervoltPlan? _plan;
    private int _tick;
    private VfCurveProbe? _probe;

    /// <summary>The card a running probe is bound to — captured at start, never re-read from _gpu.</summary>
    private GpuContext? _probeGpu;

    /// <summary>The applied-state key of the card the last probe pinned (kept after the run ends).</summary>
    private string? _probeRecordKey;

    [ObservableProperty] public partial bool ProbeRunning { get; set; }
    [ObservableProperty] public partial string ProbeStatusText { get; set; } = string.Empty;

    // Capability term for non-NVIDIA GPUs only — the NVIDIA gate is unchanged.
    public bool CanApply => _services.DemoMode
        || (_services.IsElevated && _gpu is not null
            && (_gpu.Vendor == Core.Hardware.GpuVendor.Nvidia
                || _gpu.Tuner.Capabilities.SupportsCoreOffset
                || _gpu.Tuner.Capabilities.SupportsLockedCoreClock));

    public string GateText => CanApply
        ? string.Empty
        : _gpu is not null && _gpu.Vendor != Core.Hardware.GpuVendor.Nvidia
            && !_gpu.Tuner.Capabilities.SupportsCoreOffset
            && !_gpu.Tuner.Capabilities.SupportsLockedCoreClock
            ? "Undervolting isn't implemented for this GPU yet — the offset and clock-lock knobs it needs are unavailable in this beta."
            : "Applying an undervolt needs administrator rights.";

    // Only mentions the stored table (gold line, per-point editor) where this driver exposes it.
    public string MethodNote => SupportsVfPoints
        ? "Two curves: the gold dashed line is the driver's stored V/F table, which you can edit point by " +
          "point below; the blue line is what the GPU actually did, including the effect of the power limit " +
          "and throttling, which the stored table can't show. Bar shading is how long the GPU spent at each " +
          "voltage. However you undervolt (clicking the chart or editing points), test the result on the " +
          "Stability page before you trust it."
        : "The blue line is what the GPU actually did at each voltage, including the effect of the power " +
          "limit and throttling; bar shading is how long it spent there. Clicking the chart computes a " +
          "lock + offset undervolt from it. Test the result on the Stability page before you trust it.";

    public VfCurveViewModel(AppServices services)
    {
        _services = services;
        _gpu = services.SelectedGpu;
        services.Telemetry.SnapshotTaken += OnSnapshot;
        WatchTuner();
        TrackTuningEpochs();
        RefreshCurve();
        RefreshVfPoints();
    }

    // --- Measurement epochs (R-20) -------------------------------------------
    // Every card's curve fills from telemetry whether or not this page is on
    // screen, and a plan is only valid against samples taken under the tuning
    // it builds on. So every card's recorder is told the tuning its card runs
    // — now, and after every write to that card from anywhere — and sets its
    // old samples aside when that tuning moved. OnTargetPicked re-reads it
    // before planning, which also catches a change made by another program.

    private void TrackTuningEpochs()
    {
        if (_services.DemoMode)
        {
            // The demo curve is synthetic stock data, made at stock by construction.
            _services.VfCurve.AdoptTuningState(VfTuningState.Stock);
            return;
        }

        foreach (var gpu in _services.Gpus)
        {
            _services.VfCurveFor(gpu.Index).SetTuningState(VfTuningState.Read(gpu.Tuner));
            gpu.Tuner.StateWritten += OnAnyTunerWritten;
        }
    }

    // Raised on the writing thread after the tuner released its lock. Reading
    // the new tuning is a driver call, so it happens on the UI thread, never in
    // the writer's path. The probe's own pins land here too and change nothing
    // (a clock lock is not part of the tuning a curve depends on).
    private void OnAnyTunerWritten(IGpuTuner tuner) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_services.Gpus.FirstOrDefault(g => ReferenceEquals(g.Tuner, tuner)) is { } gpu
                && _services.VfCurveFor(gpu.Index).SetTuningState(VfTuningState.Read(tuner))
                && gpu.Index == _gpu?.Index)
            {
                RefreshCurve();
            }
        });

    /// <summary>Re-reads the selected card's tuning into its curve; true when old samples were set aside.</summary>
    private bool RestampSelected() =>
        _gpu is { } gpu && Recorder.SetTuningState(VfTuningState.Read(gpu.Tuner));

    // --- Per-point curve editor --------------------------------------------
    // Shown on whatever GPU answers the clock-boost interfaces: the capability
    // is probed live (GpuTuner.DiscoverCapabilities), never assumed from the
    // generation — it is verified on RTX 50 and expected on RTX 20/30/40 (F-51).

    // --- Writes made elsewhere (F-08) ----------------------------------------
    // The table was re-read only by this page's own commands, so after a TDR or
    // panic reset, a profile apply or a Tuning-page apply the gold curve, the
    // point readout and the offset box kept showing a table the card no longer
    // had. Every tuner write now announces itself; this page re-reads on it.

    private IGpuTuner? _watchedTuner;

    private void WatchTuner()
    {
        var tuner = _gpu?.Tuner;
        if (ReferenceEquals(tuner, _watchedTuner))
        {
            return;
        }

        if (_watchedTuner is not null)
        {
            _watchedTuner.StateWritten -= OnTunerStateWritten;
        }

        _watchedTuner = tuner;
        if (tuner is not null)
        {
            tuner.StateWritten += OnTunerStateWritten;
        }
    }

    // Raised on the writing thread after the tuner released its lock; hop to
    // the UI thread without blocking the writer. This page's own commands
    // re-read synchronously right after their write, so their echo finds an
    // unchanged table and does nothing.
    private void OnTunerStateWritten(IGpuTuner tuner) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_gpu?.Tuner, tuner))
            {
                RefreshVfPoints(announceOutsideChange: true);
            }
        });

    private IReadOnlyList<Core.Interop.Nvapi.NvapiGpu.VfpTablePoint> _vfpPoints = [];

    [ObservableProperty] public partial IReadOnlyList<VfBin>? DriverCurve { get; set; }
    [ObservableProperty] public partial double VfpPointSlider { get; set; }
    [ObservableProperty] public partial string VfpPointText { get; set; } = "—";
    [ObservableProperty] public partial string VfpOffsetText { get; set; } = "0";
    [ObservableProperty] public partial string VfpTargetClockText { get; set; } = string.Empty;
    [ObservableProperty] public partial string VfpStatusText { get; set; } = string.Empty;

    public bool SupportsVfPoints => _gpu?.Tuner.Capabilities.SupportsVfPoints == true;

    public double VfpPointMax => Math.Max(0, _vfpPoints.Count - 1);

    public string VfPointsNote { get; } =
        "Direct edits to the driver's stored V/F table — the mechanism Afterburner's curve editor uses. " +
        "Verified working on RTX 50 (5090, driver 616.56); expected to work on RTX 20/30/40 with the same " +
        "interfaces. The global core offset shares this table, so applying a core offset rewrites every " +
        "point at once and erases per-point edits — set the offset first, shape the curve second. Clearing " +
        "point offsets left the global core offset in place on the 5090 tested; the status line reports what " +
        "every point reads afterwards on yours. Applying any profile also removes point offsets that profile " +
        "doesn't carry, so the curve matches what you applied. Every write, clear included, is checked by " +
        "reading the table back, and the status line says so when that readback isn't possible. Validate " +
        "with the Stability page afterwards — a point that applies is not a point that's stable.";

    /// <param name="announceOutsideChange">
    /// Re-read because a write happened somewhere else: do nothing if the table
    /// is unchanged (so text being typed survives), and say so if it changed.
    /// </param>
    private void RefreshVfPoints(bool announceOutsideChange = false)
    {
        if (!SupportsVfPoints || _gpu is null)
        {
            _vfpPoints = [];
            DriverCurve = null;
            return;
        }

        if (_gpu.Tuner.TryReadVfPoints(out var points) != Core.Interop.Nvapi.NvapiStatus.Ok)
        {
            _vfpPoints = [];
            DriverCurve = null;
            return;
        }

        if (announceOutsideChange)
        {
            if (points.SequenceEqual(_vfpPoints))
            {
                return;
            }

            VfpStatusText = "The table changed outside this editor (a reset, a profile, the Tuning page or a " +
                            "stability run) — the curve, the point readout and the offset box were re-read from the card.";
        }

        _vfpPoints = points;
        DriverCurve = points
            .Select(p => new VfBin(p.VoltageMv, p.ClockMHz + p.OffsetMHz, p.ClockMHz + p.OffsetMHz, 1))
            .ToArray();
        OnPropertyChanged(nameof(VfpPointMax));
        UpdateVfpPointText();
    }

    private Core.Interop.Nvapi.NvapiGpu.VfpTablePoint? SelectedVfpPoint =>
        _vfpPoints.Count == 0
            ? null
            : _vfpPoints[Math.Clamp((int)Math.Round(VfpPointSlider), 0, _vfpPoints.Count - 1)];

    partial void OnVfpPointSliderChanged(double value) => UpdateVfpPointText();

    private void UpdateVfpPointText()
    {
        if (SelectedVfpPoint is not { } point)
        {
            VfpPointText = "—";
            return;
        }

        VfpPointText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{point.VoltageMv:F0} mV → {point.ClockMHz:F0} MHz stored, offset {point.OffsetMHz:+0;-0;0} MHz (slot {point.Index})");
        VfpOffsetText = point.OffsetMHz.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void ApplyVfPointOffset()
    {
        if (_gpu is null || SelectedVfpPoint is not { } point)
        {
            return;
        }

        if (!int.TryParse(VfpOffsetText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int offset))
        {
            VfpStatusText = "Offset must be a whole number of MHz.";
            return;
        }

        // Refused here, not clamped downstream (F-10): typing 9000 used to write
        // +1500 and report it "verified". The tuner re-checks against the table.
        var request = new Dictionary<int, int> { [point.Index] = offset };
        if (VfPointPlanner.ValidatePointOffsets(request, liveSlots: null) is string refusal)
        {
            VfpStatusText = refusal;
            return;
        }

        var result = _gpu.Tuner.SetVfPointOffsets(request);
        VfpStatusText = $"{result.Knob}: {(result.Applied ? "ok" : "FAILED")} — {result.Detail}";
        RefreshVfPoints();
    }

    [RelayCommand]
    private void ApplyVfFlatten()
    {
        if (_gpu is null || SelectedVfpPoint is not { } point)
        {
            return;
        }

        // NumberStyles.Float accepts "NaN" and "Infinity"; a NaN target reached
        // the planner as an all-zero plan that wiped every point (R-21). The
        // planner refuses it too — this says so in the box's own terms.
        if (!double.TryParse(VfpTargetClockText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double targetClock)
            || !double.IsFinite(targetClock))
        {
            VfpStatusText = "Enter the target clock in MHz (e.g. 1875).";
            return;
        }

        var plan = Core.Tuning.VfPointPlanner.PlanFlatten(_vfpPoints, point.VoltageMv, targetClock, out string? refusal);
        if (plan is null)
        {
            VfpStatusText = refusal ?? "No plan.";
            return;
        }

        var result = _gpu.Tuner.SetVfPointOffsets(plan.OffsetsMHz);
        string lower = plan.LowerPointsCapped > 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $", {plan.LowerPointsCapped} lower points brought down to the target")
            : string.Empty;
        VfpStatusText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Flatten @ {plan.AnchorVoltageMv:F0} mV → {plan.TargetClockMHz:F0} MHz ({plan.PointsFlattened} higher points capped{lower}): " +
            $"{(result.Applied ? "ok" : "FAILED")} — {result.Detail}");
        RefreshVfPoints();
    }

    [RelayCommand]
    private void ClearVfPoints()
    {
        if (_gpu is null)
        {
            return;
        }

        var result = _gpu.Tuner.ClearVfPointOffsets();
        VfpStatusText = $"{result.Knob}: {(result.Applied ? "ok" : "FAILED")} — {result.Detail}";
        RefreshVfPoints();
    }

    /// <summary>
    /// The UI moved to another GPU: show its curve and plan against its ranges.
    /// A probe already in flight keeps the card it started on (its tuner,
    /// sampler, load and recorder were bound at start); it is never silently
    /// redirected and never silently cancelled — the status line names that card
    /// for as long as it differs from the selected one.
    /// </summary>
    public void RebindGpu()
    {
        var previousGpu = _gpu;
        _gpu = _services.SelectedGpu;
        WatchTuner();
        _plan = null;
        HasPlan = false;
        PlanText = string.Empty;
        TargetVoltage = 0;
        TargetClock = 0;

        // The V/F-point apply result is this card's, like every other verdict on
        // every other page. It was the one string here that survived a switch.
        if (previousGpu is not null && previousGpu.Index != _gpu?.Index)
        {
            VfpStatusText = string.Empty;
        }
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(GateText));
        OnPropertyChanged(nameof(SupportsVfPoints));
        OnPropertyChanged(nameof(MethodNote));

        if (ProbeRunning && _probeGpu is { } probing && probing.Index != _gpu?.Index)
        {
            ProbeStatusText =
                $"Probe still running on GPU {probing.Index} — {probing.Name}; it finishes on that card, and " +
                "its readings are that card's. The curve below is the selected card's.";
        }
        else if (!ProbeRunning)
        {
            // Don't leave the previous card's verdict standing over this card's curve.
            ProbeStatusText = string.Empty;
        }

        RefreshCurve();
        RefreshVfPoints();
    }

    private volatile bool _isLive;

    /// <inheritdoc />
    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (_isLive == value)
            {
                return;
            }

            _isLive = value;
            if (value && _services.Telemetry.TryHistoryFor(LiveDeviceIndex)?.Latest is { } latest)
            {
                ApplySnapshot(latest);
                RefreshCurve();
            }
        }
    }

    private uint LiveDeviceIndex => _gpu?.Index ?? 0;

    private void OnSnapshot(GpuSnapshot snapshot)
    {
        // Display only: the curve recorder samples every tick in AppServices,
        // and a running probe samples on its own thread.
        if (!_isLive || snapshot.DeviceIndex != LiveDeviceIndex)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ApplySnapshot(snapshot);

            // Redraw the curve a few times a minute; sampling itself is continuous.
            if (++_tick % 5 == 0)
            {
                RefreshCurve();
            }
        });
    }

    private void ApplySnapshot(GpuSnapshot snapshot)
    {
        LiveVoltage = snapshot.CoreVoltageMv ?? 0;
        LiveClock = snapshot.CoreClockMHz ?? 0;
    }

    private void RefreshCurve()
    {
        var recorder = Recorder;
        Curve = recorder.GetCurve();
        PeakSamples = recorder.PeakBinSamples();
        SampleText = Curve.Count switch
        {
            // "Collecting…" is advice to wait, and on a GPU whose driver reports
            // no core voltage the wait is endless: a V/F curve is voltage against
            // clock, so there is nothing to collect. The verified Arc B390 sat on
            // that message indefinitely. The recorder counts the reads that
            // arrived without a voltage, so this is measured, not assumed.
            0 when recorder.VoltageSensorLooksAbsent =>
                (_gpu?.CoreVoltageUnavailableReason is { } noSource
                    ? $"Afterglow cannot read this GPU's core voltage ({noSource})"
                    : "This GPU's driver has reported no core voltage in any sample so far")
                + ", so a V/F curve cannot be measured here. Clock, power and utilisation are still live on the Dashboard.",
            0 => "Collecting… run a game or the burn test to draw the curve.",
            _ => $"{Curve.Count} voltage points from {recorder.TotalSamples:N0} samples under load",
        };

    }

    /// <summary>Called by the chart when the user picks a target point.</summary>
    public void OnTargetPicked(double voltageMv, double clockMHz)
    {
        // A chart with a degenerate axis maps the pointer to NaN or infinity;
        // nothing downstream may treat that as a target (R-21).
        if (!double.IsFinite(voltageMv) || !double.IsFinite(clockMHz))
        {
            _plan = null;
            HasPlan = false;
            PlanText = "That point could not be read off the chart — click inside the plotted area.";
            return;
        }

        TargetVoltage = Math.Round(voltageMv / 5) * 5;
        TargetClock = Math.Round(clockMHz / 15) * 15;

        // Plan against the tuning the card runs NOW, and only from samples
        // taken under it (R-20): the read also sets older samples aside if the
        // tuning moved without a notification reaching this page.
        var recorder = Recorder;
        var tuning = _gpu is { } gpu ? VfTuningState.Read(gpu.Tuner) : VfTuningState.Stock;
        bool setAside = _gpu is not null && recorder.SetTuningState(tuning);
        _plan = recorder.PlanUndervolt(TargetVoltage, TargetClock, tuning, _gpu?.Tuner.Capabilities);

        if (_plan is null)
        {
            HasPlan = false;
            PlanText = setAside
                ? $"No plan for {TargetVoltage:F0} mV / {TargetClock:F0} MHz — this card's tuning changed since the " +
                  $"curve was measured (it now runs {tuning.Describe()}), so those samples were set aside: a plan " +
                  "may only use samples taken under the tuning it builds on. Keep gaming or run the probe to rebuild it."
                : tuning.PointShape is not null
                    ? $"No plan for {TargetVoltage:F0} mV / {TargetClock:F0} MHz — the stored V/F table carries " +
                      "per-point edits. A plan is a single core offset, which would erase them, and the measured " +
                      "clock at this voltage includes that point's own edit, so the offset cannot be worked out " +
                      "from it. Clear the point offsets below, let the curve rebuild, then plan."
                    : $"No plan for {TargetVoltage:F0} mV / {TargetClock:F0} MHz — either the curve has too " +
                      "few samples near that voltage yet (keep gaming or run the probe), or the required " +
                      "offset/lock would fall outside what this GPU's driver accepts.";
            if (setAside)
            {
                RefreshCurve();
            }

            return;
        }

        HasPlan = true;
        PlanText = _plan.Describe();
    }

    [RelayCommand]
    private void ApplyPlan()
    {
        if (_plan is null)
        {
            return;
        }

        if (_gpu is null)
        {
            ApplyResultText = "Applied (demo mode — no hardware was changed).";
            LastApplyFailed = false;
            return;
        }

        var caps = _gpu.Tuner.Capabilities;
        var current = _gpu.Tuner.ReadCurrent();
        var profile = new Core.Profiles.TuningProfile
        {
            Name = "V/F undervolt",
            CoreOffsetMHz = Math.Clamp(_plan.CoreOffsetMHz, caps.CoreOffsetMinMHz, caps.CoreOffsetMaxMHz),
            MemOffsetMHz = current.MemOffsetMHz,
            VoltageBoostPct = current.VoltageBoostPct,
            LockedCoreClockMHz = _plan.LockClockMHz,
        };

        var result = _gpu.Tuner.Apply(profile);
        ApplyResultText = result.Summary;
        LastApplyFailed = !result.AllSucceeded;

        // The samples behind this plan were measured under the old offset;
        // the curve starts again under the new one (R-20). Said here because
        // the chart empties the moment it does.
        if (RestampSelected())
        {
            ApplyResultText += " The measured curve restarts under the new tuning — keep gaming or run the probe " +
                               "before planning again.";
            RefreshCurve();
        }

        // The core offset just written lives in the stored table too: show it.
        // (The Tuning page learns of this write through GpuTuner.StateWritten.)
        RefreshVfPoints();
    }

    /// <summary>Asks a running probe to stop, without waiting.</summary>
    public void RequestProbeStop() => _probe?.Cancel();

    /// <summary>
    /// Cancels a running probe and waits for its worker to put the clock state
    /// back. Called from application shutdown: the probe pins the core clock at
    /// an exact frequency and only its own finally block undoes that, so the
    /// process must not exit out from under it.
    /// </summary>
    /// <returns>
    /// True when the probe's worker finished — its restore outcome is then on
    /// file in the applied-state store. False when it was still unwinding at
    /// the timeout: the card may be leaving pinned with nothing recorded.
    /// </returns>
    public bool CancelProbeAndWait(TimeSpan timeout)
    {
        if (_probe is not { } probe)
        {
            return true;
        }

        // The join is the only signal this ViewModel has to give. The pin's
        // record is the tuner's: written before the pin lands, resolved on a
        // verified release, kept through the clean-shutdown mark — so a worker
        // still unwinding at the timeout leaves the truthful record behind. An
        // in-memory copy of "which cards are still pinned" fell out of step
        // with the store the moment the user pressed Reset, and re-recorded a
        // pin the tuner had just released.
        return probe.CancelAndWait(timeout);
    }

    /// <summary>
    /// App exit: cancel a running probe, give its restore a bounded moment to
    /// land, then say whether a pin may still be on the card. False means App
    /// must not record a clean shutdown (same contract as the stepper, F-27).
    /// The answer comes from the tuner's pin record, not from this run: the
    /// tuner writes that record before every pin and resolves it only on a
    /// verified release, so it stays right after a Reset freed the card and
    /// while a slow load teardown (now after the restore) is still running.
    /// </summary>
    public bool StopForShutdown()
    {
        if (_probe is { } probe)
        {
            _ = probe.CancelAndWait(TimeSpan.FromSeconds(10));
        }

        return _probeRecordKey is not { } key
            || AppliedStateStore.Load(key) is not { ProbeLockPending: true };
    }

    /// <summary>
    /// Maps the whole curve in a minute or two (each step samples until its
    /// voltage can drive a plan, R-47): locks the clock at each step under
    /// load and records the voltage the driver selects. Restores the previous
    /// lock state when done.
    /// </summary>
    [RelayCommand]
    private void ToggleProbe()
    {
        if (ProbeRunning)
        {
            _probe?.Cancel();
            return;
        }

        // Bind the whole probe to ONE card, captured here: _gpu moves with the
        // title-bar selector, but a probe that locked clocks on this card must
        // keep sampling this card, loading this card, and saving into this
        // card's curve until it finishes. Reading _gpu per sample would report
        // another card's voltage as this card's measured V/F point.
        var gpu = _gpu;
        if (gpu is null)
        {
            ProbeStatusText = _services.DemoMode
                ? "Probe unavailable in demo mode — the demo curve is synthetic."
                : "No GPU available.";
            return;
        }

        var recorder = _services.VfCurveFor(gpu.Index);

        // Don't lock this GPU's clock through a full sweep to measure something
        // the driver cannot report. The probe pins the core clock at each step —
        // a real intervention on the user's hardware — and on a card with no
        // core-voltage sensor every step would record nothing. Refusing is not an
        // assumption: the recorder has been fed live samples since launch and
        // counts the ones that arrived without a voltage.
        // Name the real cause: on NVIDIA without an NVAPI pairing the driver
        // does report voltage — Afterglow has no way to read it — and blaming
        // the driver steered users away from the actual fix.
        if (gpu.CoreVoltageUnavailableReason is { } noSource)
        {
            ProbeStatusText =
                $"Afterglow cannot read this GPU's core voltage: {noSource}. A V/F probe would lock the "
                + "clock through a full sweep and measure nothing. Not started.";
            return;
        }

        if (recorder.VoltageSensorLooksAbsent)
        {
            ProbeStatusText =
                "This GPU's driver has reported no core voltage in any sample so far, so a V/F probe would "
                + "lock the clock through a full sweep and measure nothing. Not started.";
            return;
        }

        _probeGpu = gpu;
        try
        {
            _probeRecordKey = gpu.Tuner.RecordKey;
        }
        catch (InvalidOperationException)
        {
            _probeRecordKey = null; // a tuner with no key keeps no record to consult
        }

        _probe = new VfCurveProbe(gpu.Tuner, () => gpu.Poller.Poll())
        {
            TargetPciBusId = gpu.PciBusId,
            TargetVendorId = gpu.PciVendorId,
        };
        _probe.ProgressChanged += progress =>
        {
            // The pin's record is written by the tuner the moment the pin lands
            // (Core); nothing here has to survive a closing dispatcher.
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                // Name the probed card whenever it is no longer the one on
                // screen: these volts are not the selected card's.
                string card = _gpu is { } selected && selected.Index == gpu.Index
                    ? string.Empty
                    : $" [GPU {gpu.Index} — {gpu.Name}]";

                ProbeRunning = progress.Running;

                // A failed clock-lock restore means the GPU may still be pinned
                // at an exact frequency — full clock and voltage at idle, until
                // an explicit unlock or a reboot. It is deliberately independent
                // of the coverage outcome, so a fully measured sweep can carry
                // it; appending it to every terminal message stops "previous
                // clock state restored" being printed over a GPU that is not.
                string restoreWarning = !progress.Running && progress.RestoreFailed
                    ? $"  ⚠ {progress.Phase}"
                    : string.Empty;

                // A hardware fault the load detected — during the sweep, or while
                // winding down after the clock restore — is about the GPU under
                // load, not about shutdown: a real finding that survives a fully
                // measured sweep.
                if (!progress.Running && progress.LoadFailure is { } loadFault)
                {
                    restoreWarning += $"  ⚠ {loadFault}";
                }

                // Measured is not the same as plannable (R-47): a step whose
                // voltage bin stayed under the plan minimum cannot drive an
                // undervolt yet, and "complete" alone implied it could.
                string coverageNote = progress.StepsBelowPlanCoverage > 0
                    ? $" {progress.StepsBelowPlanCoverage} of {progress.StepIndex} steps collected fewer than " +
                      $"{VfCurveRecorder.PlanMinBinSamples} samples at their voltage, so an undervolt can't be planned " +
                      "there yet — run the probe again or keep gaming to fill them in."
                    : string.Empty;

                ProbeStatusText = progress.Running
                    ? progress.MeasuredVoltageMv is double mv
                        ? $"Step {progress.StepIndex}/{progress.StepCount}: {progress.TargetClockMHz} MHz measured at {mv:F0} mV{card}"
                        : $"Step {progress.StepIndex + 1}/{progress.StepCount}: locking {progress.TargetClockMHz} MHz…{card}"
                    // Gate on the outcome, not on the phase string. Matching
                    // "complete" meant every early exit — a refused clock lock
                    // above all — fell through to the completed wording and was
                    // presented as a full measured V/F map.
                    : progress.Outcome switch
                    {
                        VfProbeOutcome.Completed => (card.Length == 0
                            ? "Probe complete — the curve below is your GPU's measured V/F map."
                            : $"Probe complete on GPU {gpu.Index} — {gpu.Name}; the readings went to that card's curve. The curve below is the selected card's.")
                            + coverageNote
                            + restoreWarning,

                        // Somebody else wrote this card mid-sweep (R-02). The
                        // probe stopped there rather than pin over that write.
                        VfProbeOutcome.Interrupted =>
                            $"Probe stopped after {progress.StepIndex} of {progress.StepCount} steps: this card's tuning " +
                            "was changed elsewhere (a reset, a profile, a hotkey, a rule or another page) while it ran, " +
                            "so it did not pin the clock again. The curve holds only the steps measured before that." +
                            $"{card}{restoreWarning}",
                        VfProbeOutcome.Cancelled =>
                            $"Probe cancelled after {progress.StepIndex} of {progress.StepCount} steps; " +
                            (progress.RestoreFailed
                                ? "the previous clock state could NOT be restored."
                                : "previous clock state restored.") +
                            $" The curve holds only the steps measured so far.{card}{restoreWarning}",
                        _ =>
                            $"Probe stopped after {progress.StepIndex} of {progress.StepCount} steps — {progress.Phase}. " +
                            $"The curve is incomplete and is not a full V/F map.{card}{restoreWarning}",
                    };
                if (!progress.Running)
                {
                    _probeGpu = null;
                    RefreshCurve();
                }
            });
        };

        // An unresolved pin on another card stays on record in the store; only
        // a clean probe of THAT card, or a Reset, resolves it.
        var unresolved = AppliedStateStore.LoadAll()
            .Where(s => s.ProbeLockPending && s.GpuUuid is not null && s.GpuUuid != gpu.StableKey)
            .Select(s => s.GpuUuid!)
            .ToList();
        if (unresolved.Count > 0)
        {
            Core.Diagnostics.Log.Warn(
                $"Starting a probe on GPU {gpu.Index} while an earlier probe's clock lock is still unresolved " +
                $"({string.Join(", ", unresolved)}); " +
                "the unresolved locks stay on record until a probe on that card releases cleanly.");
        }

        ProbeRunning = true;
        ProbeStatusText = "Starting probe…";
        _probe.Start(recorder);
    }

    [RelayCommand]
    private void ResetCurve()
    {
        Recorder.Clear();
        if (!Recorder.Save())
        {
            SampleText = "Curve cleared in memory, but the curve file on disk belongs to another GPU (or could " +
                         "not be written), so it was left as it was.";
        }

        _plan = null;
        HasPlan = false;
        PlanText = string.Empty;
        TargetVoltage = 0;
        TargetClock = 0;
        RefreshCurve();
        ApplyResultText = "Curve cleared — it rebuilds as the GPU runs under load.";
        LastApplyFailed = false;
    }
}
