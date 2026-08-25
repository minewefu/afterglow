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

    [RelayCommand]
    private void ToggleStress()
    {
        if (StressRunning)
        {
            _stress?.Stop();
            return;
        }

        _stress?.Dispose();
        _stress = new GpuStressTest { IterationsPerDispatch = (uint)Intensity };
        _stress.ProgressChanged += progress =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnStressProgress(progress));
        StressFailed = false;
        StressRunning = true;
        StressStatusText = "Burning…";
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
                StressStatusText = "Burning — all results verified correct so far.";
                break;
            case StressState.Stopped:
                StressRunning = false;
                StressStatusText = $"Stopped after {progress.Elapsed:hh\\:mm\\:ss} with 0 errors — stable under this load.";
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

        _stepper = new StabilityStepper(_gpu.Tuner);
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
