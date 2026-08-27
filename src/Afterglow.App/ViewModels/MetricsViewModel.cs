using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core.Metrics;

namespace Afterglow.App.ViewModels;

public partial class MetricsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _foregroundPoll;

    [ObservableProperty] private bool _captureRunning;
    [ObservableProperty] private string _captureStatusText;
    [ObservableProperty] private bool _presentMonMissing;

    [ObservableProperty] private string _appText = "No presenting app detected yet";
    [ObservableProperty] private string _presentModeText = string.Empty;
    [ObservableProperty] private string _avgFpsText = "—";
    [ObservableProperty] private string _p1Text = "—";
    [ObservableProperty] private string _p01Text = "—";
    [ObservableProperty] private string _low1Text = "—";
    [ObservableProperty] private string _low01Text = "—";
    [ObservableProperty] private string _avgFrametimeText = "—";
    [ObservableProperty] private string _maxFrametimeText = "—";
    [ObservableProperty] private string _frameCountText = string.Empty;
    [ObservableProperty] private IReadOnlyList<double>? _frametimeSeries;

    public string MethodNote { get; } =
        "Averages are harmonic (N·1000/Σft). P1/P0.1 are interpolated percentiles of the frametime " +
        "distribution. 1%/0.1% lows average the worst 1%/0.1% of frames (the Gamers Nexus/CapFrameX method). " +
        "Rolling 30-second window.";

    private readonly Random _demoRandom = new(7);
    private readonly List<double> _demoFrametimes = [];

    private readonly SessionReportStore _sessionStore = new();
    private DateTimeOffset _captureStartedAt;
    private (string App, FrameWindowStats Stats)? _lastTargetStats;

    public System.Collections.ObjectModel.ObservableCollection<SessionReport> SessionHistory { get; } = [];

    public string SessionHistoryNote { get; } =
        "Every capture of 30 s or more is recorded here with the offsets that were applied — run the same " +
        "game before and after a tuning change and the comparison writes itself. FPS numbers are the trailing " +
        "30 s window at capture end; power/temps are averaged over the session.";

    [RelayCommand]
    private void CopySessionHistory()
    {
        if (SessionHistory.Count > 0)
        {
            Clipboard.SetText(SessionReportStore.ToMarkdown([.. SessionHistory]));
        }
    }

    private void LoadSessionHistory()
    {
        SessionHistory.Clear();
        foreach (var report in _sessionStore.LoadAll().Reverse())
        {
            SessionHistory.Add(report);
        }
    }

    private void RecordSession()
    {
        int duration = (int)(DateTimeOffset.Now - _captureStartedAt).TotalSeconds;
        if (duration < 30 || _lastTargetStats is not { } last || last.Stats.FrameCount < 100)
        {
            return;
        }

        double power = 0;
        double gpuTemp = 0;
        double memTemp = 0;
        int samples = 0;
        foreach (var snapshot in _services.Telemetry.HistoryFor(0).GetAll())
        {
            if (snapshot.Timestamp < _captureStartedAt)
            {
                continue;
            }

            power += snapshot.PowerW ?? 0;
            gpuTemp += snapshot.GpuTempC ?? 0;
            memTemp += snapshot.MemJunctionTempC ?? 0;
            samples++;
        }

        int core = 0;
        int mem = 0;
        if (_services.Gpus.Count > 0)
        {
            try
            {
                var current = _services.Gpus[0].Tuner.ReadCurrent();
                core = current.CoreOffsetMHz;
                mem = current.MemOffsetMHz;
            }
            catch (InvalidOperationException)
            {
            }
        }

        var report = new SessionReport
        {
            Application = last.App,
            StartedAt = _captureStartedAt,
            DurationSeconds = duration,
            AvgFps = last.Stats.AverageFps,
            Low1Fps = last.Stats.Low1Fps,
            P1Fps = last.Stats.P1Fps,
            Frames = last.Stats.FrameCount,
            AvgPowerW = samples > 0 ? power / samples : 0,
            AvgGpuTempC = samples > 0 ? gpuTemp / samples : 0,
            AvgMemJunctionC = samples > 0 ? memTemp / samples : 0,
            CoreOffsetMHz = core,
            MemOffsetMHz = mem,
        };
        _sessionStore.Append(report);
        SessionHistory.Insert(0, report);
        _services.Flight?.Marker($"session-recorded app={report.Application} fps={report.AvgFps:F1}");
    }

    public MetricsViewModel(AppServices services)
    {
        _services = services;
        _presentMonMissing = !services.DemoMode && !File.Exists(PresentMonSession.DefaultExePath);
        _captureStatusText = services.DemoMode
            ? "Demo mode — the numbers below are synthetic, showing what live capture looks like."
            : _presentMonMissing
                ? $"PresentMon binary not found ({PresentMonSession.BundledExeName}). FPS capture is disabled — reinstall Afterglow or place Intel PresentMon under ThirdParty\\PresentMon."
                : "Capture idle.";

        if (services.DemoMode)
        {
            _captureRunning = true;
        }

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();

        LoadSessionHistory();

        _foregroundPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _foregroundPoll.Tick += (_, _) =>
            _services.FrameMetrics.ForegroundProcessId = ForegroundProcess.GetForegroundProcessId();
        _foregroundPoll.Start();
    }

    [RelayCommand]
    private void ToggleCapture()
    {
        if (CaptureRunning)
        {
            RecordSession();
            _services.FrameMetrics.Session.Dispose();
            CaptureRunning = false;
            CaptureStatusText = "Capture stopped.";
            return;
        }

        if (_services.FrameMetrics.Start())
        {
            CaptureRunning = true;
            _captureStartedAt = DateTimeOffset.Now;
            _lastTargetStats = null;
            CaptureStatusText = "Capturing present events for all processes (ETW).";
        }
        else
        {
            CaptureStatusText = _services.FrameMetrics.Session.FailureReason ?? "Could not start capture.";
            PresentMonMissing = !File.Exists(PresentMonSession.DefaultExePath);
        }
    }

    private void Refresh()
    {
        if (_services.DemoMode)
        {
            RefreshDemo();
            return;
        }

        if (!CaptureRunning)
        {
            return;
        }

        if (_services.FrameMetrics.Session.State == PresentMonState.Failed)
        {
            CaptureRunning = false;
            CaptureStatusText = _services.FrameMetrics.Session.FailureReason ?? "Capture failed.";
            return;
        }

        var stats = _services.FrameMetrics.GetTargetStats();
        if (stats is null)
        {
            AppText = "No presenting app detected yet";
            PresentModeText = string.Empty;
            return;
        }

        var (app, s) = stats.Value;
        _lastTargetStats = (app.Application, s);
        AppText = $"{app.Application} (PID {app.ProcessId})";
        PresentModeText = app.PresentMode;
        AvgFpsText = s.AverageFps.ToString("F1");
        P1Text = s.P1Fps.ToString("F1");
        P01Text = s.P01Fps.ToString("F1");
        Low1Text = s.Low1Fps.ToString("F1");
        Low01Text = s.Low01Fps.ToString("F1");
        AvgFrametimeText = $"{s.AverageFrametimeMs:F2} ms";
        MaxFrametimeText = $"{s.MaxFrametimeMs:F1} ms";
        FrameCountText = $"{s.FrameCount:N0} frames in window";
        FrametimeSeries = _services.FrameMetrics.GetTargetFrametimes(600);
    }

    private void RefreshDemo()
    {
        // Synthetic ~240 fps trace with occasional stutter spikes.
        for (int i = 0; i < 240; i++)
        {
            double ft = 4.17 + (_demoRandom.NextDouble() - 0.5) * 0.9;
            if (_demoRandom.NextDouble() < 0.004)
            {
                ft += _demoRandom.NextDouble() * 14;
            }

            _demoFrametimes.Add(ft);
        }

        while (_demoFrametimes.Count > 7200)
        {
            _demoFrametimes.RemoveRange(0, _demoFrametimes.Count - 7200);
        }

        if (Afterglow.Core.Metrics.FrameMetrics.Compute(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_demoFrametimes)) is not { } s)
        {
            return;
        }

        AppText = "Demo game (synthetic frametimes)";
        PresentModeText = "Hardware: Independent Flip (simulated)";
        AvgFpsText = s.AverageFps.ToString("F1");
        P1Text = s.P1Fps.ToString("F1");
        P01Text = s.P01Fps.ToString("F1");
        Low1Text = s.Low1Fps.ToString("F1");
        Low01Text = s.Low01Fps.ToString("F1");
        AvgFrametimeText = $"{s.AverageFrametimeMs:F2} ms";
        MaxFrametimeText = $"{s.MaxFrametimeMs:F1} ms";
        FrameCountText = $"{s.FrameCount:N0} frames in window";
        FrametimeSeries = _demoFrametimes.TakeLast(600).ToArray();
    }
}
