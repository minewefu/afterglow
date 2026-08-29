using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core.Hardware;
using Afterglow.Core.Stress;

namespace Afterglow.App.ViewModels;

public partial class StabilityViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly GpuContext? _gpu;
    private GpuStressTest? _stress;
    private StabilityStepper? _stepper;

    [ObservableProperty] private bool _stressRunning;
    [ObservableProperty] private string _stressStatusText = "Idle. The burn test loads the GPU with a deterministic compute workload and verifies every result bit-for-bit.";
    [ObservableProperty] private bool _stressFailed;
    [ObservableProperty] private string _stressRateText = string.Empty;
    [ObservableProperty] private string _stressElapsedText = string.Empty;
    [ObservableProperty] private double _intensity = 4096;

    /// <summary>0 = sustained burn, 1 = transition cycling, 2 = boost excursions.</summary>
    [ObservableProperty] private int _patternIndex;

    public string PatternDescription => PatternIndex switch
    {
        1 => "Cycles between load and idle to force P-state and memory-clock transitions — the regime " +
             "where memory offsets fail even though they pass sustained burns. VRAM contents are " +
             "re-verified across every transition; a mismatch there is transition corruption, caught red-handed.",
        2 => "Short saturating bursts with idle gaps: each burst rides the boost overshoot through the top " +
             "clock bins before power management clamps, sweeping the full clock range dozens of times a " +
             "minute — the bursty desktop regime where raw core offsets crash even after passing burns.",
        _ => "Continuous full load with bit-exact verification — validates sustained clocks and power. " +
             "Note: passing here does not validate light-load boost or clock transitions; run all three " +
             "patterns before trusting a daily overclock.",
    };

    private VramTest? _vram;

    [ObservableProperty] private bool _vramRunning;
    [ObservableProperty] private bool _vramFailed;
    [ObservableProperty] private string _vramStatusText =
        "Idle. Fills as much VRAM as the OS safely allows with a deterministic pattern and verifies " +
        "every element on the GPU; alternate rounds invert the pattern so every bit is exercised both ways.";
    [ObservableProperty] private string _vramStatsText = string.Empty;

    [RelayCommand]
    private void ToggleVram()
    {
        if (VramRunning)
        {
            _vram?.Stop();
            return;
        }

        _vram?.Dispose();
        _vram = new VramTest { TargetPciBusId = _gpu?.PciBusId };
        _vram.ProgressChanged += progress =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnVramProgress(progress));
        VramFailed = false;
        VramRunning = true;
        VramStatusText = "Allocating…";
        _services.Flight?.Marker("vram-start");
        _vram.Start();
    }

    private void OnVramProgress(VramProgress progress)
    {
        double gib = progress.PlannedBytes / (double)(1L << 30);
        VramStatsText = progress.State == StressState.Running
            ? $"{gib:F1} GiB · round {progress.Rounds + 1} · {progress.GiBPerSecond:F0} GiB/s verified"
            : string.Empty;

        switch (progress.State)
        {
            case StressState.Running:
                VramStatusText = "Testing — every element is written and read back on the GPU.";
                break;
            case StressState.Stopped:
                VramRunning = false;
                VramStatusText = progress.Rounds >= 1
                    ? $"Stopped after {progress.Elapsed:hh\\:mm\\:ss}: {gib:F1} GiB × {progress.Rounds} full " +
                      "rounds, 0 errors — VRAM is stable at the current memory clocks."
                    : $"Stopped after {progress.Elapsed:hh\\:mm\\:ss} before a full round completed — run " +
                      "longer for a verdict.";
                break;
            case StressState.ArtifactDetected:
            case StressState.DeviceLost:
            case StressState.Failed:
                VramRunning = false;
                VramFailed = true;
                VramStatusText = progress.Detail ?? "VRAM test failed.";
                break;
            default:
                break;
        }

        if (progress.State is not (StressState.Running or StressState.Idle))
        {
            _services.Flight?.Marker($"vram-end state={progress.State} rounds={progress.Rounds}");
        }
    }

    [ObservableProperty] private bool _stepperRunning;
    [ObservableProperty] private string _stepperPhaseText = string.Empty;
    [ObservableProperty] private double _stepperProgress;
    [ObservableProperty] private string _stepperLog = string.Empty;
    [ObservableProperty] private double _stepMHz = 30;
    [ObservableProperty] private double _secondsPerStep = 60;
    [ObservableProperty] private double _maxOffset = 300;

    public bool CanStep => !_services.DemoMode && _services.IsElevated && _gpu is not null;

    public string StepGateText => CanStep
        ? string.Empty
        : "The stepper applies clock offsets, so it needs a real GPU and administrator rights.";

    public string IntensityLabel => $"{Intensity:F0} iterations/dispatch";

    [ObservableProperty]
    private string _liveStatsText = string.Empty;

    public bool HasCrashReport => _services.LastCrashReport is not null;

    public string CrashReportHeadline => _services.LastCrashReport?.Headline ?? string.Empty;

    public string CrashReportText => _services.LastCrashReport?.ReportText ??
        "No crash captured. If a session ever ends in a hard reset, the flight recorder's last seconds " +
        "are correlated with the Windows event log and the postmortem appears here.";

    [RelayCommand]
    private void CopyCrashReport()
    {
        if (_services.LastCrashReport is { } report)
        {
            Clipboard.SetText(report.ReportText);
        }
    }

    public StabilityViewModel(AppServices services)
    {
        _services = services;
        _gpu = services.Gpus.Count > 0 ? services.Gpus[0] : null;
        services.Telemetry.SnapshotTaken += OnSnapshot;
    }

    private void OnSnapshot(Core.Telemetry.GpuSnapshot snapshot)
    {
        uint deviceIndex = _gpu?.Index ?? 0;
        if (snapshot.DeviceIndex != deviceIndex)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            string hot = snapshot.HotSpotTempC is double hs ? $" (hot {hs:F0}°)" : string.Empty;
            string mem = snapshot.MemJunctionTempC is double mj ? $" · mem {mj:F0}°C" : string.Empty;
            LiveStatsText =
                $"{snapshot.CoreClockMHz ?? 0} MHz · {snapshot.GpuTempC ?? 0}°C{hot}{mem} · " +
                $"{snapshot.PowerW ?? 0:F0} W · fans {snapshot.MaxFanPercent ?? 0}%";
        });
    }

    partial void OnIntensityChanged(double value) => OnPropertyChanged(nameof(IntensityLabel));

    partial void OnPatternIndexChanged(int value) => OnPropertyChanged(nameof(PatternDescription));

    private StressPattern SelectedPattern => PatternIndex switch
    {
        1 => StressPattern.Transitions,
        2 => StressPattern.BoostExcursions,
        _ => StressPattern.Sustained,
    };

    [RelayCommand]
    private void ToggleStress()
    {
        if (StressRunning)
        {
            _stress?.Stop();
            return;
        }

        _stress?.Dispose();
        _stress = new GpuStressTest
        {
            IterationsPerDispatch = (uint)Intensity,
            Pattern = SelectedPattern,
            TargetPciBusId = _gpu?.PciBusId,
        };
        _stress.ProgressChanged += progress =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnStressProgress(progress));
        StressFailed = false;
        StressRunning = true;
        StressStatusText = "Burning…";
        _services.Flight?.Marker($"stress-start pattern={SelectedPattern}");
        _stress.Start();
    }

    private void OnStressProgress(StressProgress progress)
    {
        StressElapsedText = $"{progress.Elapsed:hh\\:mm\\:ss}";
        StressRateText = progress.DispatchesPerSecond > 0
            ? $"{progress.DispatchesPerSecond:F1} dispatches/s"
            : string.Empty;

        switch (progress.State)
        {
            case StressState.Running:
                StressStatusText = progress.Phase switch
                {
                    "load" => $"Transition cycling — load phase. {progress.Transitions} transitions verified clean so far.",
                    "idle" => $"Transition cycling — idle phase (clocks dropping). {progress.Transitions} transitions verified clean so far.",
                    "burst" => $"Boost excursions — riding the boost overshoot into the top clock bins. " +
                               $"{progress.Transitions} excursions verified clean so far.",
                    _ => "Burning — all results verified correct so far.",
                };
                break;
            case StressState.Stopped:
                StressRunning = false;
                StressStatusText = progress.Transitions > 0
                    ? $"Stopped after {progress.Elapsed:hh\\:mm\\:ss} with 0 errors across {progress.Transitions} " +
                      "clock excursions — stable in this regime."
                    : $"Stopped after {progress.Elapsed:hh\\:mm\\:ss} with 0 errors — stable under this load.";
                break;
            case StressState.ArtifactDetected:
                StressRunning = false;
                StressFailed = true;
                StressStatusText = progress.Detail ?? "Computation errors detected — unstable.";
                break;
            case StressState.DeviceLost:
                StressRunning = false;
                StressFailed = true;
                StressStatusText = progress.Detail ?? "GPU driver reset during load — unstable.";
                break;
            case StressState.Failed:
                StressRunning = false;
                StressFailed = true;
                StressStatusText = $"Stress test could not run: {progress.Detail}";
                break;
            default:
                break;
        }

        if (progress.State is not (StressState.Running or StressState.Idle))
        {
            _services.Flight?.Marker($"stress-end state={progress.State} transitions={progress.Transitions}");
        }
    }

    [RelayCommand]
    private void ToggleStepper()
    {
        if (StepperRunning)
        {
            _stepper?.Cancel();
            return;
        }

        if (_gpu is null)
        {
            return;
        }

        _stepper = new StabilityStepper(_gpu.Tuner) { TargetPciBusId = _gpu.PciBusId };
        _stepper.StatusChanged += status =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnStepperStatus(status));
        StepperRunning = true;
        _stepper.Start(new StepperOptions
        {
            StepMHz = (int)StepMHz,
            SecondsPerStep = (int)SecondsPerStep,
            MaxOffsetMHz = (int)MaxOffset,
        });
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _stepper?.Cancel();
        _stress?.Dispose();
        _vram?.Dispose();
    }

    private void OnStepperStatus(StepperStatus status)
    {
        StepperRunning = status.Running;
        StepperPhaseText = status.Running
            ? $"Testing {(status.CurrentOffsetMHz >= 0 ? "+" : string.Empty)}{status.CurrentOffsetMHz} MHz — " +
              $"{status.StepElapsed:mm\\:ss} / {status.StepDuration:mm\\:ss}"
            : status.ResultOffsetMHz is int result
                ? $"Finished — stable core offset: {(result >= 0 ? "+" : string.Empty)}{result} MHz"
                : $"{status.Phase}";
        StepperProgress = status.StepDuration.TotalSeconds > 0
            ? Math.Clamp(status.StepElapsed.TotalSeconds / status.StepDuration.TotalSeconds, 0, 1)
            : 0;
        StepperLog = string.Join(Environment.NewLine, status.Log.TakeLast(14));
    }
}
