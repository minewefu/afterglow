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
        _gpu = _services.SelectedGpu;
        _plan = null;
        HasPlan = false;
        PlanText = string.Empty;
        TargetVoltage = 0;
        TargetClock = 0;
        VfpStatusText = string.Empty;
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
        SampleText = Curve.Count == 0
            ? "Collecting… run a game or the burn test to draw the curve."
            : $"{Curve.Count} voltage points from {recorder.TotalSamples:N0} samples under load";
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
        _probeGpu = gpu;
        _probe = new VfCurveProbe(gpu.Tuner, () => gpu.Poller.Poll())
        {
            TargetPciBusId = gpu.PciBusId,
            TargetVendorId = gpu.PciVendorId,
        };
        _probe.ProgressChanged += progress =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                // Name the probed card whenever it is no longer the one on
                // screen: these volts are not the selected card's.
                string card = _gpu is { } selected && selected.Index == gpu.Index
                    ? string.Empty
                    : $" [GPU {gpu.Index} — {gpu.Name}]";

                ProbeRunning = progress.Running;
                ProbeStatusText = progress.Running
                    ? progress.MeasuredVoltageMv is double mv
                        ? $"Step {progress.StepIndex}/{progress.StepCount}: {progress.TargetClockMHz} MHz measured at {mv:F0} mV{card}"
                        : $"Step {progress.StepIndex + 1}/{progress.StepCount}: locking {progress.TargetClockMHz} MHz…{card}"
                    : progress.Phase switch
                    {
                        "complete" => card.Length == 0
                            ? "Probe complete — the curve below is your GPU's measured V/F map."
                            : $"Probe complete on GPU {gpu.Index} — {gpu.Name}; the readings went to that card's curve. The curve below is the selected card's.",
                        "cancelled" => $"Probe cancelled; previous clock state restored.{card}",
                        _ => $"Probe stopped: {progress.Phase}{card}",
                    };
                if (!progress.Running)
                {
                    _probeGpu = null;
                    RefreshCurve();
                }
            });

        ProbeRunning = true;
        ProbeStatusText = "Starting probe…";
        _probe.Start(recorder);
    }

    [RelayCommand]
    private void ResetCurve()
    {
        Recorder.Clear();
        Recorder.Save();
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
