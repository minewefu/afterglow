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

    [ObservableProperty] private bool _probeRunning;
    [ObservableProperty] private string _probeStatusText = string.Empty;

    public bool CanApply => _services.DemoMode || (_services.IsElevated && _gpu is not null);

    public string GateText => CanApply
        ? string.Empty
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
        "interfaces. Note the global core offset lives in this same table (it shifts every point), so " +
        "clearing all point offsets also returns the core offset to 0. Every write is verified by reading " +
        "the table back. Validate with the Stability page afterwards — a point that applies is not a point " +
        "that's stable.";

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

    /// <summary>The UI moved to another GPU: show its curve and plan against its ranges.</summary>
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

        if (_gpu is null)
        {
            ProbeStatusText = _services.DemoMode
                ? "Probe unavailable in demo mode — the demo curve is synthetic."
                : "No GPU available.";
            return;
        }

        _probe = new VfCurveProbe(_gpu.Tuner, () => _gpu.Poller.Poll()) { TargetPciBusId = _gpu.PciBusId };
        _probe.ProgressChanged += progress =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                ProbeRunning = progress.Running;
                ProbeStatusText = progress.Running
                    ? progress.MeasuredVoltageMv is double mv
                        ? $"Step {progress.StepIndex}/{progress.StepCount}: {progress.TargetClockMHz} MHz measured at {mv:F0} mV"
                        : $"Step {progress.StepIndex + 1}/{progress.StepCount}: locking {progress.TargetClockMHz} MHz…"
                    : progress.Phase switch
                    {
                        "complete" => "Probe complete — the curve below is your GPU's measured V/F map.",
                        "cancelled" => "Probe cancelled; previous clock state restored.",
                        _ => $"Probe stopped: {progress.Phase}",
                    };
                if (!progress.Running)
                {
                    RefreshCurve();
                }
            });

        ProbeRunning = true;
        ProbeStatusText = "Starting probe…";
        _probe.Start(Recorder);
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
