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

    [ObservableProperty] public partial bool CaptureRunning { get; set; }
    [ObservableProperty] public partial string CaptureStatusText { get; set; }
    [ObservableProperty] public partial bool PresentMonMissing { get; set; }

    [ObservableProperty] public partial string AppText { get; set; } = "No presenting app detected yet";
    [ObservableProperty] public partial string PresentModeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string AvgFpsText { get; set; } = "—";
    [ObservableProperty] public partial string P1Text { get; set; } = "—";
    [ObservableProperty] public partial string P01Text { get; set; } = "—";
    [ObservableProperty] public partial string Low1Text { get; set; } = "—";
    [ObservableProperty] public partial string Low01Text { get; set; } = "—";
    [ObservableProperty] public partial string AvgFrametimeText { get; set; } = "—";
    [ObservableProperty] public partial string MaxFrametimeText { get; set; } = "—";
    [ObservableProperty] public partial string FrameCountText { get; set; } = string.Empty;
    [ObservableProperty] public partial IReadOnlyList<double>? FrametimeSeries { get; set; }

    public string MethodNote { get; } =
        "Averages are harmonic (N·1000/Σft). P1/P0.1 are interpolated percentiles of the frametime " +
        "distribution. 1%/0.1% lows average the worst 1%/0.1% of frames (the Gamers Nexus/CapFrameX method). " +
        "Rolling 30-second window.";

    private readonly Random _demoRandom = new(7);
    private readonly List<double> _demoFrametimes = [];

    private readonly SessionReportStore _sessionStore = new();
    private DateTimeOffset _captureStartedAt;

    /// <summary>
    /// The target's last live statistics in this capture: its process, and when
    /// they were read (<see cref="Environment.TickCount64"/>, the tuning
    /// timeline's clock).
    /// </summary>
    private (string App, int Pid, FrameWindowStats Stats, long AtMs)? _lastTargetStats;

    /// <summary>The tuning the capture ran under, read as it goes (R-28).</summary>
    private CaptureTuningTimeline _tuning = new();

    public System.Collections.ObjectModel.ObservableCollection<SessionReport> SessionHistory { get; } = [];

    public string SessionHistoryNote { get; } =
        "Every capture of 30 s or more is recorded here with the offsets that were applied. Ctrl+click two " +
        "sessions to compare them. FPS numbers are the trailing 30 s window at capture end; power/temps are " +
        "averaged over the session.";

    [ObservableProperty] public partial string CompareText { get; set; } = string.Empty;

    public bool HasComparison => CompareText.Length > 0;

    partial void OnCompareTextChanged(string value) => OnPropertyChanged(nameof(HasComparison));

    private SessionReport? _compareA;
    private SessionReport? _compareB;

    /// <summary>Called from the view when the session list selection changes.</summary>
    public void OnSessionSelectionChanged(IReadOnlyList<SessionReport> selected)
    {
        if (selected.Count != 2)
        {
            _compareA = null;
            _compareB = null;
            CompareText = selected.Count > 2
                ? "Select exactly two sessions to compare."
                : string.Empty;
            return;
        }

        _compareA = selected[0];
        _compareB = selected[1];
        CompareText = SessionCompare.Describe(_compareA, _compareB);
    }

    [RelayCommand]
    private void CopyComparison()
    {
        if (_compareA is { } a && _compareB is { } b)
        {
            ClipboardSafe.Copy(SessionCompare.ToMarkdown(a, b));
        }
    }

    [RelayCommand]
    private void CopySessionHistory()
    {
        if (SessionHistory.Count > 0)
        {
            ClipboardSafe.Copy(SessionReportStore.ToMarkdown([.. SessionHistory]));
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

    /// <summary>The GPU the running capture was started on (see ToggleCapture).</summary>
    private Core.Hardware.GpuContext? _captureGpu;

    /// <summary>
    /// Adds a reading of the capture's card to the tuning timeline. The capture's
    /// card, not the selector's: the selector can move to another GPU mid-game,
    /// and the report used to stamp that card's offsets under the first card's
    /// name and averages. No card (FPS capture needs none) reads as nothing.
    /// </summary>
    private void ReadCaptureTuning() =>
        _tuning.AddReading(Environment.TickCount64, _captureGpu is { } gpu ? TuningReading.Read(gpu.Tuner) : null);

    /// <returns>The recorded report, or null when the capture was too short to record.</returns>
    private SessionReport? RecordSession()
    {
        int duration = (int)(DateTimeOffset.Now - _captureStartedAt).TotalSeconds;
        if (duration < 30 || _lastTargetStats is not { } last || last.Stats.FrameCount < 100)
        {
            return null;
        }

        // FPS capture needs no supported GPU; with none there is no history
        // for the device, and a throw here would leave the capture unstoppable
        // (F-18). The averages come from the card the capture started on.
        var captureGpu = _captureGpu ?? _services.SelectedGpu;
        var telemetry = Core.Metrics.SessionTelemetry.Since(
            _services.Telemetry, captureGpu?.Index ?? 0, _captureStartedAt);

        // The tuning in force while these frames were presented, from the
        // timeline Refresh kept — never a read of the card now (R-28). In the
        // game-profile workflow the game's exit has already restored the
        // baseline by the time Stop is clicked, and reading it here stamped the
        // tuned run with the untuned settings. Offsets stay null, not 0, for a
        // knob the card does not have (an Arc session exported "core 0 / mem 0
        // MHz" for offsets it cannot apply).
        var tuning = _tuning.AttributeTo(last.Pid, last.AtMs, last.Stats);

        var report = new SessionReport
        {
            Application = last.App,
            GpuName = captureGpu?.Name,
            GpuUuid = captureGpu?.Uuid,
            StartedAt = _captureStartedAt,
            DurationSeconds = duration,
            AvgFps = last.Stats.AverageFps,
            Low1Fps = last.Stats.Low1Fps,
            P1Fps = last.Stats.P1Fps,
            Frames = last.Stats.FrameCount,
            AvgPowerW = telemetry.PowerW,
            AvgGpuTempC = telemetry.GpuTempC,
            AvgMemJunctionC = telemetry.MemJunctionC,
            CoreOffsetMHz = tuning.Reading?.CoreOffsetMHz,
            MemOffsetMHz = tuning.Reading?.MemOffsetMHz,
            TuningChangedDuringWindow = tuning.Reading is null ? null : tuning.ChangedDuringWindow,
        };
        _sessionStore.Append(report);
        SessionHistory.Insert(0, report);
        _services.Flight?.Marker($"session-recorded app={report.Application} fps={report.AvgFps:F1}");
        return report;
    }

    public MetricsViewModel(AppServices services)
    {
        _services = services;
        PresentMonMissing = !services.DemoMode && !File.Exists(PresentMonSession.DefaultExePath);
        CaptureStatusText = services.DemoMode
            ? "Demo mode — the numbers below are synthetic, showing what live capture looks like."
            : PresentMonMissing
                ? $"PresentMon binary not found ({PresentMonSession.BundledExeName}). FPS capture is disabled — reinstall Afterglow or place Intel PresentMon under ThirdParty\\PresentMon."
                : "Capture idle.";

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += (_, _) => Refresh();

        LoadSessionHistory();

        _foregroundPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _foregroundPoll.Tick += (_, _) =>
            _services.FrameMetrics.ForegroundProcessId = ForegroundProcess.GetForegroundProcessId();

        // Last: CaptureRunning is a notifying property now, and its change hook
        // starts the timers created just above.
        if (services.DemoMode)
        {
            CaptureRunning = true;
        }
    }

    partial void OnCaptureRunningChanged(bool value) => UpdateCaptureTimers(value);

    /// <summary>
    /// Both timers only mean something while frames are being captured (the
    /// foreground app picks the capture target); without a capture they used to
    /// wake the UI thread three times every two seconds, for days, in the tray.
    /// They keep running while capturing even when this page is hidden — the
    /// dashboard tile, the overlay and the session record read the same stats.
    /// </summary>
    private void UpdateCaptureTimers(bool capturing)
    {
        if (capturing)
        {
            _services.FrameMetrics.ForegroundProcessId = ForegroundProcess.GetForegroundProcessId();
            _refresh.Start();
            _foregroundPoll.Start();
        }
        else
        {
            _refresh.Stop();
            _foregroundPoll.Stop();
        }
    }

    [RelayCommand]
    private void ToggleCapture()
    {
        if (CaptureRunning)
        {
            EndCapture(endedOnItsOwn: null);
            return;
        }

        if (_services.FrameMetrics.Start())
        {
            CaptureRunning = true;
            _captureStartedAt = DateTimeOffset.Now;
            _lastTargetStats = null;

            // Bind the capture to the card selected when it STARTED. Reading the
            // selector again at Stop attributed a whole session's sensor averages
            // to whichever card the user had switched to since.
            _captureGpu = _services.SelectedGpu;

            // The tuning in force as the capture begins opens its timeline.
            _tuning = new CaptureTuningTimeline();
            ReadCaptureTuning();
            CaptureStatusText = "Capturing present events for all processes (ETW).";
        }
        else
        {
            CaptureStatusText = _services.FrameMetrics.Session.FailureReason ?? "Could not start capture.";
            PresentMonMissing = !File.Exists(PresentMonSession.DefaultExePath);
        }
    }

    /// <summary>
    /// Ends the capture, recording its session first. <paramref name="endedOnItsOwn"/>
    /// is null when the user pressed Stop, else why PresentMon stopped by itself:
    /// a capture that ended early still measured what it measured, so it is
    /// recorded like any other (the same 30 s / 100 frame minimum applies) and
    /// the status says so.
    /// </summary>
    private void EndCapture(string? endedOnItsOwn)
    {
        SessionReport? recorded = null;
        try
        {
            recorded = RecordSession();
        }
        finally
        {
            // Stopping never depends on the report: whatever goes wrong
            // writing it, the capture ends and the button leaves the
            // running state instead of rethrowing on every click (F-18).
            // The service's Stop, not just the session's: it also drops the
            // capture's frame windows (R-26).
            _services.FrameMetrics.Stop();
            CaptureRunning = false;

            string status = endedOnItsOwn is null ? "Capture stopped." : $"Capture ended: {endedOnItsOwn}";
            if (endedOnItsOwn is not null && recorded is not null)
            {
                status += " The session up to that point was recorded.";
            }

            if (recorded?.TuningChangedDuringWindow == true)
            {
                status += " The tuning changed during its last 30 s, so that session's numbers mix two configurations.";
            }

            CaptureStatusText = status;
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

        // Every terminal state ends the capture (R-29). Only Failed was handled,
        // so a PresentMon that exited — its trace stopped by another capture
        // under the once-shared session name, or a crash — left the page
        // labelled "Capturing" over a capture that had ended.
        var session = _services.FrameMetrics.Session;
        if (session.State != PresentMonState.Running)
        {
            EndCapture(session.FailureReason ?? (session.State == PresentMonState.Failed
                ? "Capture failed."
                : "PresentMon exited."));
            return;
        }

        // Tuning first, then the stats, then whether their process still runs:
        // that order is what lets the report place a tuning change before or
        // after the game's last frame (R-28, see CaptureTuningTimeline).
        ReadCaptureTuning();
        var stats = _services.FrameMetrics.GetTargetStats();
        if (stats is null)
        {
            // Clear the numbers too. Blanking only the header left the last
            // app's FPS, percentiles, frametimes and graph on screen under
            // "No presenting app detected yet" — a frozen reading that looks
            // live, and the one thing this app must never show. The same shape
            // of bug had already been fixed in two other places; this was the
            // third consumer of the same null.
            AppText = "No presenting app detected yet";
            PresentModeText = string.Empty;
            ClearFrameStats();
            return;
        }


        var (app, s) = stats.Value;
        _lastTargetStats = (app.Application, app.ProcessId, s, Environment.TickCount64);
        if (_services.FrameMetrics.IsStillRunning(app.ProcessId))
        {
            _tuning.ConfirmRunning(app.ProcessId);
        }

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

    /// <summary>Blanks every measured frame statistic — used whenever there is
    /// nothing being measured, so no stale number survives on screen.</summary>
    private void ClearFrameStats()
    {
        // Display only. _lastTargetStats is the completed capture's RECORD, and
        // RecordSession() refuses to write a session without it — nulling it here
        // meant a game that quit two seconds before the user pressed Stop threw
        // the whole measurement away with no message. ToggleCapture already
        // resets it per capture.
        AvgFpsText = "—";
        P1Text = "—";
        P01Text = "—";
        Low1Text = "—";
        Low01Text = "—";
        AvgFrametimeText = "—";
        MaxFrametimeText = "—";
        FrameCountText = string.Empty;
        FrametimeSeries = [];
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
