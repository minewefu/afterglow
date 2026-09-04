using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core.Hardware;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.App.ViewModels;

public partial class VfCurveViewModel : ObservableObject
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

    [ObservableProperty] private IReadOnlyList<VfBin>? _curve;
    [ObservableProperty] private long _peakSamples;
    [ObservableProperty] private double _liveVoltage;
    [ObservableProperty] private double _liveClock;
    [ObservableProperty] private double _targetVoltage;
    [ObservableProperty] private double _targetClock;
    [ObservableProperty] private string _sampleText = string.Empty;

    [ObservableProperty]
    private string _planText =
        "Click a point on the curve to target it. Afterglow computes the exact core offset and clock lock " +
        "that hold that clock at that voltage — pick a point up and to the left of the curve for an undervolt.";
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private string _applyResultText = string.Empty;
    [ObservableProperty] private bool _lastApplyFailed;

    private UndervoltPlan? _plan;
    private int _tick;
    private VfCurveProbe? _probe;

    /// <summary>The card a running probe is bound to — captured at start, never re-read from _gpu.</summary>
    private GpuContext? _probeGpu;

    [ObservableProperty] private bool _probeRunning;
    [ObservableProperty] private string _probeStatusText = string.Empty;

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

    public string MethodNote { get; } =
        "Two curves, two truths: the gold dashed line is the driver's stored V/F table (editable per point " +
        "below), and the blue measured curve is what the GPU actually did — including the power limit and " +
        "throttling, which the stored table cannot show. Bar shading is how much time was spent at each " +
        "voltage. Click the chart to compute a lock+offset undervolt from measured reality, or edit the " +
        "stored table point-by-point; either way, validate on the Stability page before trusting it.";

    public VfCurveViewModel(AppServices services)
    {
        _services = services;
        _gpu = services.SelectedGpu;
        services.Telemetry.SnapshotTaken += OnSnapshot;
        RefreshCurve();
        RefreshVfPoints();
    }

    // --- Per-point curve editor (Turing/Ampere/Ada) --------------------------

    private IReadOnlyList<Core.Interop.Nvapi.NvapiGpu.VfpTablePoint> _vfpPoints = [];

    [ObservableProperty] private IReadOnlyList<VfBin>? _driverCurve;
    [ObservableProperty] private double _vfpPointSlider;
    [ObservableProperty] private string _vfpPointText = "—";
    [ObservableProperty] private string _vfpOffsetText = "0";
    [ObservableProperty] private string _vfpTargetClockText = string.Empty;
    [ObservableProperty] private string _vfpStatusText = string.Empty;

    public bool SupportsVfPoints => _gpu?.Tuner.Capabilities.SupportsVfPoints == true;

    public double VfpPointMax => Math.Max(0, _vfpPoints.Count - 1);

    public string VfPointsNote { get; } =
        "Direct edits to the driver's stored V/F table — the mechanism Afterburner's curve editor uses. " +
        "Verified working on RTX 50 (5090, driver 616.56); expected to work on RTX 20/30/40 with the same " +
        "interfaces. The global core offset shares this table, so applying a core offset rewrites every " +
        "point at once and erases per-point edits — set the offset first, shape the curve second. Applying " +
        "any profile also removes point offsets that profile doesn't carry, so the curve matches what you " +
        "applied. Every write is verified by reading the table back. Validate with the Stability page " +
        "afterwards — a point that applies is not a point that's stable.";

    private void RefreshVfPoints()
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

        var result = _gpu.Tuner.SetVfPointOffsets(new Dictionary<int, int> { [point.Index] = offset });
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

        if (!double.TryParse(VfpTargetClockText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double targetClock))
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
        VfpStatusText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Flatten @ {plan.AnchorVoltageMv:F0} mV → {plan.TargetClockMHz:F0} MHz ({plan.PointsFlattened} points capped): " +
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

    private void OnSnapshot(GpuSnapshot snapshot)
    {
        uint index = _gpu?.Index ?? 0;
        if (snapshot.DeviceIndex != index)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LiveVoltage = snapshot.CoreVoltageMv ?? 0;
            LiveClock = snapshot.CoreClockMHz ?? 0;

            // Redraw the curve a few times a minute; sampling itself is continuous.
            if (++_tick % 5 == 0)
            {
                RefreshCurve();
            }
        });
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
        TargetVoltage = Math.Round(voltageMv / 5) * 5;
        TargetClock = Math.Round(clockMHz / 15) * 15;

        int currentOffset = _gpu?.Tuner.ReadCurrent().CoreOffsetMHz ?? 0;
        _plan = Recorder.PlanUndervolt(TargetVoltage, TargetClock, currentOffset, _gpu?.Tuner.Capabilities);

        if (_plan is null)
        {
            HasPlan = false;
            PlanText = $"No plan for {TargetVoltage:F0} mV / {TargetClock:F0} MHz — either the curve has too " +
                       "few samples near that voltage yet (keep gaming or run the probe), or the required " +
                       "offset/lock would fall outside what this GPU's driver accepts.";
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

        // The join is the only signal this ViewModel has to give: a worker
        // that finished has already persisted its own restore failure (and the
        // store keeps that record through the clean-shutdown mark), while a
        // worker that has not finished is the one case shutdown must record
        // for it (see ActiveProbeKey). An in-memory copy of "which cards are
        // still pinned" fell out of step with the store the moment the user
        // pressed Reset, and re-recorded a pin the tuner had just released.
        return probe.CancelAndWait(timeout);
    }

    /// <summary>
    /// Stable key of the card a probe is running on, for a shutdown that has
    /// to record a pin the probe had no time to report.
    /// </summary>
    public string? ActiveProbeKey => _probeGpu?.StableKey;

    /// <summary>
    /// True unless the running probe may still have a pin on the card (see
    /// <see cref="VfCurveProbe.ClockStateSettled"/>).
    /// </summary>
    public bool ActiveProbeClockSettled => _probe?.ClockStateSettled ?? true;

    /// <summary>
    /// Maps the whole curve in about a minute: locks the clock at each step under
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

        _probe = new VfCurveProbe(gpu.Tuner, () => gpu.Poller.Poll())
        {
            TargetPciBusId = gpu.PciBusId,
            TargetVendorId = gpu.PciVendorId,
            StableKey = gpu.StableKey,
        };
        _probe.ProgressChanged += progress =>
        {
            // The pending record for a failed restore is written by the probe
            // itself (Core) at the moment it fails; nothing here has to survive
            // a closing dispatcher.
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

                // A hardware fault the load detected while winding down is about
                // the top clock this sweep pinned, not about shutdown — it is a
                // real finding, and it survives a fully measured sweep.
                if (!progress.Running && progress.LoadFailure is { } loadFault)
                {
                    restoreWarning += $"  ⚠ {loadFault}";
                }

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
                            + restoreWarning,
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
