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
    private readonly GpuContext? _gpu;

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
        "NVIDIA does not expose the stored V/F curve on RTX 50, and rejects per-point curve writes there — so " +
        "Afterglow measures the curve instead: every voltage/clock pair the GPU actually runs is recorded, " +
        "keeping the highest clock seen at each voltage. That shows the real curve including your offset, the " +
        "power limit, and any throttling — a static curve read cannot. Bar shading is how much time was spent " +
        "at each voltage. Pick a point to compute the exact offset + clock lock that holds that clock at that " +
        "voltage; validate it on the Stability page before trusting it.";

    public VfCurveViewModel(AppServices services)
    {
        _services = services;
        _gpu = services.Gpus.Count > 0 ? services.Gpus[0] : null;
        services.Telemetry.SnapshotTaken += OnSnapshot;
        RefreshCurve();
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
        var recorder = _services.VfCurve;
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
        _plan = _services.VfCurve.PlanUndervolt(TargetVoltage, TargetClock, currentOffset, _gpu?.Tuner.Capabilities);

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

        _probe = new VfCurveProbe(_gpu.Tuner, () => _gpu.Poller.Poll());
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
        _probe.Start(_services.VfCurve);
    }

    [RelayCommand]
    private void ResetCurve()
    {
        _services.VfCurve.Clear();
        _services.VfCurve.Save();
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
