using System.Security.Principal;
using Afterglow.Core;
using Afterglow.Core.Fans;
using Afterglow.Core.Hardware;
using Afterglow.Core.Metrics;
using Afterglow.Core.Profiles;
using Afterglow.Core.Services;
using Afterglow.Core.Settings;
using Afterglow.Core.Telemetry;

namespace Afterglow.App;

/// <summary>
/// Composition root: owns every long-lived service. Built once at startup,
/// disposed on exit (which restores firmware fan control and marks the
/// applied-state file as a clean shutdown).
/// </summary>
public sealed class AppServices : IDisposable
{
    public required bool DemoMode { get; init; }

    public required bool IsElevated { get; init; }

    public GpuManager? Manager { get; init; }

    public required IReadOnlyList<GpuContext> Gpus { get; init; }

    public required TelemetryService Telemetry { get; init; }

    public required ProfileStore Profiles { get; init; }

    public required IReadOnlyDictionary<uint, FanControlService> FanControl { get; init; }

    public required FrameMetricsService FrameMetrics { get; init; }

    public CsvLogger? ActiveCsvLogger { get; set; }

    /// <summary>Global shortcuts; the app attaches them to its window, Settings shows and edits them.</summary>
    public Services.HotkeyService Hotkeys { get; } = new();

    public string DriverVersion { get; init; } = "—";

    public required GameWatcher GameWatcher { get; init; }

    public required TdrWatchdog TdrWatchdog { get; init; }

    /// <summary>Per-GPU measured voltage/frequency curves, each fed only its own card's telemetry.</summary>
    public required IReadOnlyDictionary<uint, Core.Tuning.VfCurveRecorder> VfCurves { get; init; }

    private readonly Core.Tuning.VfCurveRecorder _fallbackCurve = new();

    /// <summary>The primary GPU's curve (compat convenience; empty recorder when no GPU).</summary>
    public Core.Tuning.VfCurveRecorder VfCurve =>
        Gpus.Count > 0 && VfCurves.TryGetValue(Gpus[0].Index, out var primary)
            ? primary
            : VfCurves.TryGetValue(0, out var demo) ? demo : _fallbackCurve;

    /// <summary>Curve recorder for one device (falls back to the primary's/demo's).</summary>
    public Core.Tuning.VfCurveRecorder VfCurveFor(uint deviceIndex) =>
        VfCurves.TryGetValue(deviceIndex, out var recorder) ? recorder : VfCurve;

    private uint _selectedGpuIndex;

    /// <summary>Device index of the GPU the UI is currently driving.</summary>
    public uint SelectedGpuIndex => _selectedGpuIndex;

    /// <summary>The GPU the UI is currently driving (null in demo mode / no hardware).</summary>
    public GpuContext? SelectedGpu =>
        Gpus.FirstOrDefault(g => g.Index == _selectedGpuIndex) ?? (Gpus.Count > 0 ? Gpus[0] : null);

    /// <summary>Raised on the UI thread after the selection moved to another GPU.</summary>
    public event Action<GpuContext>? SelectedGpuChanged;

    private bool _windowVisible;

    /// <summary>
    /// True while the main window is on screen (shown and not minimized). Pages
    /// stop their per-tick display work while it is false — see ILivePage.
    /// </summary>
    public bool WindowVisible => _windowVisible;

    /// <summary>Raised on the UI thread when <see cref="WindowVisible"/> flips.</summary>
    public event Action<bool>? WindowVisibleChanged;

    public void SetWindowVisible(bool visible)
    {
        if (visible == _windowVisible)
        {
            return;
        }

        _windowVisible = visible;
        WindowVisibleChanged?.Invoke(visible);
    }

    public void SelectGpu(uint deviceIndex)
    {
        if (deviceIndex == _selectedGpuIndex || Gpus.All(g => g.Index != deviceIndex))
        {
            return;
        }

        _selectedGpuIndex = deviceIndex;
        SelectedGpuChanged?.Invoke(SelectedGpu!);
    }

    /// <summary>
    /// Turns the CSV-only sensors (video clock, encoder/decoder, PCIe) on while a
    /// CSV log is recording and off otherwise; nothing else in the app shows them.
    /// </summary>
    public void SetExtendedMetrics(bool enabled)
    {
        foreach (var gpu in Gpus)
        {
            gpu.Poller.ExtendedMetrics = enabled;
        }
    }

    /// <summary>Settings key for one GPU's fan configuration.</summary>
    public static string FanKeyFor(GpuContext gpu) => gpu.StableKey;

    /// <summary>
    /// The persisted fan configuration for one GPU: its own entry when present,
    /// else the legacy single-GPU field for the primary card, else defaults.
    /// </summary>
    public FanSettings FanSettingsFor(GpuContext gpu)
    {
        if (Settings.FansByGpu.TryGetValue(FanKeyFor(gpu), out var own))
        {
            return own;
        }

        return Gpus.Count > 0 && gpu.Index == Gpus[0].Index ? Settings.Fans : new FanSettings();
    }

    /// <summary>Per-GPU telemetry black boxes (empty in demo mode). Primary = flight\, others = flight\gpu&lt;N&gt;\.</summary>
    public IReadOnlyDictionary<uint, Core.Diagnostics.FlightRecorder> Flights { get; init; } =
        new Dictionary<uint, Core.Diagnostics.FlightRecorder>();

    /// <summary>
    /// The primary GPU's black box (null when recording is off). App-level
    /// event markers land here; per-GPU telemetry and offsets go to each
    /// card's own recorder.
    /// </summary>
    public Core.Diagnostics.FlightRecorder? Flight =>
        Gpus.Count > 0 && Flights.TryGetValue(Gpus[0].Index, out var primary)
            ? primary
            : Flights.Count > 0 ? Flights.Values.First() : null;

    /// <summary>Postmortem of the previous session, when it ended in a crash.</summary>
    public Core.Diagnostics.CrashReport? LastCrashReport { get; init; }

    private AppSettings _settings = new();

    public AppSettings Settings => _settings;

    /// <summary>Raised after settings are mutated and persisted.</summary>
    public event Action<AppSettings>? SettingsChanged;

    /// <summary>Mutates and persists settings; side effects (interval, rules) are applied.</summary>
    public void UpdateSettings(Func<AppSettings, AppSettings> mutate)
    {
        _settings = mutate(_settings);
        SettingsStore.Save(_settings);
        Telemetry.Interval = TimeSpan.FromMilliseconds(_settings.PollingIntervalMs);
        GameWatcher.UpdateRules(_settings.GameRules);
        SettingsChanged?.Invoke(_settings);
    }

    internal void InitializeSettings(AppSettings settings)
    {
        _settings = settings;
        Telemetry.Interval = TimeSpan.FromMilliseconds(settings.PollingIntervalMs);
        GameWatcher.UpdateRules(settings.GameRules);
    }

    public static bool CheckElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static AppServices Create(bool demoMode, bool enableBlackBox = true)
    {
        bool elevated = CheckElevated();

        if (demoMode)
        {
            var demoSource = new DemoSensorSource();
            var demoTelemetry = new TelemetryService([demoSource], TimeSpan.FromSeconds(1));
            demoSource.Backfill(demoTelemetry.HistoryFor(0), 600);
            demoTelemetry.Start();
            var demoCurve = new Core.Tuning.VfCurveRecorder();
            foreach (var snapshot in demoTelemetry.HistoryFor(0).GetAll())
            {
                demoCurve.Add(snapshot);
            }

            demoTelemetry.SnapshotTaken += demoCurve.Add;

            var demoServices = new AppServices
            {
                DemoMode = true,
                IsElevated = elevated,
                Gpus = [],
                Telemetry = demoTelemetry,
                Profiles = new ProfileStore(),
                FanControl = new Dictionary<uint, FanControlService>(),
                FrameMetrics = new FrameMetricsService(),
                DriverVersion = "demo",
                GameWatcher = new GameWatcher(),
                TdrWatchdog = new TdrWatchdog(),
                VfCurves = new Dictionary<uint, Core.Tuning.VfCurveRecorder> { [0] = demoCurve },
            };
            demoServices.InitializeSettings(new AppSettings());
            return demoServices;
        }

        var manager = new GpuManager();
        var fanControl = new Dictionary<uint, FanControlService>();
        foreach (var gpu in manager.Gpus)
        {
            fanControl[gpu.Index] = new FanControlService(gpu.Tuner);
        }

        // The GUI shows none of the CSV-only sensors; CSV logging turns them on.
        foreach (var gpu in manager.Gpus)
        {
            gpu.Poller.ExtendedMetrics = false;
        }

        var telemetry = new TelemetryService(
            manager.Gpus.Select(g => (ISensorSource)g.Poller).ToArray(),
            TimeSpan.FromSeconds(1));

        telemetry.SnapshotTaken += snapshot =>
        {
            if (fanControl.TryGetValue(snapshot.DeviceIndex, out var fans))
            {
                fans.OnSnapshot(snapshot);
            }
        };

        try
        {
            AppPaths.EnsureCreated();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // An unelevated instance can't create folders in a data root an
            // elevated run has locked down; it monitors without them (writes that
            // need them degrade on their own).
            Core.Diagnostics.Log.Info($"Data folders unavailable to this unelevated instance: {ex.Message}");
        }

        // One curve per GPU, each fed only its own card's snapshots and kept in
        // a file keyed by that card's UUID — a curve measured on one card must
        // never plan an undervolt for another. Not even the primary card reads
        // the old shared vf-curve.json: it carries no identity, and whichever
        // card enumerates first is not necessarily the card that measured it
        // (F-15, F-55).
        var vfCurves = new Dictionary<uint, Core.Tuning.VfCurveRecorder>();
        foreach (var gpu in manager.Gpus)
        {
            var curveRecorder = Core.Tuning.VfCurveRecorder.ForGpu(gpu.Index, gpu.Uuid, gpu.PciVendorId);
            curveRecorder.Load();
            vfCurves[gpu.Index] = curveRecorder;
        }

        if (manager.Gpus.Count > 0 && System.IO.File.Exists(Core.Tuning.VfCurveRecorder.DefaultPath))
        {
            Core.Diagnostics.Log.Info(
                "vf-curve.json from an earlier version is not loaded: it carries no GPU identity, so it cannot be " +
                "attributed to a card. Each card's curve rebuilds from telemetry; the old file is left in place.");
        }

        telemetry.SnapshotTaken += snapshot =>
        {
            if (vfCurves.TryGetValue(snapshot.DeviceIndex, out var curveRecorder))
            {
                curveRecorder.Add(snapshot);
            }
        };

        // Analyze the previous flight recordings BEFORE the new recorders
        // rotate them, then start this session's black boxes — one per GPU:
        // the primary keeps the original flight\ directory (full back-compat
        // with existing files and the classifier), each secondary card gets
        // flight\gpu<N>\. Only the resident instance records (screenshot/demo
        // runs pass enableBlackBox false — they'd fight the resident instance
        // over the files), and a recorder failure degrades to "no black box" —
        // diagnostics must never be the reason the app can't start.
        Core.Diagnostics.CrashReport? crashReport = null;
        var flights = new Dictionary<uint, Core.Diagnostics.FlightRecorder>();
        if (enableBlackBox)
        {
            string FlightDirFor(GpuContext gpu) =>
                manager.Gpus.Count > 0 && gpu.Index == manager.Gpus[0].Index
                    ? AppPaths.FlightDir
                    : System.IO.Path.Combine(AppPaths.FlightDir,
                        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"gpu{gpu.Index}"));

            // A machine crash ends every stream at once, so EVERY card's log has
            // to be read: stopping at the first non-null result always picked the
            // primary (iterated first) and reported ITS offsets and load state, so
            // a stock, idle primary produced "not an overclocking failure" while a
            // secondary card died under load. The card that was actually tuned at
            // the time of death names the report. Each analysis is fenced on its
            // own: whatever is in an old recording — torn, corrupted, or planted
            // before first run — must not stop the app, nor the rotation below
            // that moves the bad file out of the way (F-34). One System-log walk
            // serves every card (the scope), instead of one per card before the
            // main window exists.
            Core.Diagnostics.CrashReport? bestReport = null;
            using (Core.Diagnostics.CrashForensics.ShareSystemLogQueries())
            {
                foreach (var gpu in manager.Gpus)
                {
                    try
                    {
                        var report = Core.Diagnostics.CrashForensics.AnalyzePreviousSession(
                            FlightDirFor(gpu),
                            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"GPU {gpu.Index} — {gpu.Name}"));
                        if (report is null)
                        {
                            continue;
                        }

                        report = report with { GpuIndex = gpu.Index, GpuName = gpu.Name };
                        bestReport = bestReport is null || (report.TuningApplied && !bestReport.TuningApplied)
                            ? report
                            : bestReport;
                    }
                    catch (Exception ex)
                    {
                        Core.Diagnostics.Log.Warn($"Crash analysis skipped for GPU {gpu.Index}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            crashReport = bestReport;

            try
            {
                foreach (var gpu in manager.Gpus)
                {
                    flights[gpu.Index] = new Core.Diagnostics.FlightRecorder(FlightDirFor(gpu));
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Core.Diagnostics.Log.Info($"Flight recorder disabled for this session: {ex.Message}");
                foreach (var started in flights.Values)
                {
                    started.Dispose();
                }

                flights.Clear();
            }
        }

        if (flights.Count > 0)
        {
            var tunersByIndex = manager.Gpus.ToDictionary(g => g.Index, g => g.Tuner);
            var flightTicks = new Dictionary<uint, int>();
            telemetry.SnapshotTaken += snapshot =>
            {
                if (!flights.TryGetValue(snapshot.DeviceIndex, out var recorder))
                {
                    return;
                }

                recorder.Record(snapshot);

                // Offsets change rarely; sample once a minute per card so
                // CLI-applied tuning is captured too (dedup inside the recorder).
                flightTicks.TryGetValue(snapshot.DeviceIndex, out int tick);
                flightTicks[snapshot.DeviceIndex] = tick + 1;
                if (tick % 60 == 0 && tunersByIndex.TryGetValue(snapshot.DeviceIndex, out var tuner))
                {
                    try
                    {
                        var current = tuner.ReadCurrent();
                        recorder.RecordOffsets(current.CoreOffsetMHz, current.MemOffsetMHz);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            };
        }

        telemetry.Start();

        var services = new AppServices
        {
            DemoMode = false,
            IsElevated = elevated,
            Manager = manager,
            Gpus = manager.Gpus,
            Telemetry = telemetry,
            Profiles = new ProfileStore(),
            FanControl = fanControl,
            FrameMetrics = new FrameMetricsService(),
            DriverVersion = manager.DriverVersion ?? "—",
            GameWatcher = new GameWatcher(),
            TdrWatchdog = new TdrWatchdog(),
            VfCurves = vfCurves,
            Flights = flights,
            LastCrashReport = crashReport,
        };
        services.InitializeSettings(SettingsStore.Load());
        services.GameWatcher.Start();
        _ = services.TdrWatchdog.Start();
        return services;
    }

    private bool? _hardwareReleased;

    public void Dispose() => _ = Shutdown();

    /// <summary>
    /// Tears every service down and says whether the hardware was handed back:
    /// true only when every card's fan release succeeded (R-05). App.OnExit
    /// stamps the session clean only on true. Each step runs on its own — one
    /// that throws is logged and the rest still run: a CSV flush failing on a
    /// full or disconnected drive used to throw out of here before the fans
    /// were released, leaving a card pinned at a manual duty after exit with
    /// the session already stamped clean. Later calls return the first answer.
    /// </summary>
    public bool Shutdown()
    {
        if (_hardwareReleased is bool done)
        {
            return done;
        }

        var steps = new ShutdownSteps();
        steps.Run("game watcher", GameWatcher.Dispose);
        steps.Run("TDR watchdog", TdrWatchdog.Dispose);

        // Telemetry before the fans. Its SnapshotTaken subscription drives the
        // fan curve, and stopping the fans while snapshots are still arriving
        // let one land after the release and command the fans again — leaving
        // the card on manual fans after exit. FrameMetrics is disposed here too
        // because its teardown waits on PresentMon for up to two seconds, which
        // used to sit between the fan release and the poller still feeding it.
        steps.Run("frame metrics", FrameMetrics.Dispose);
        steps.Run("telemetry", Telemetry.Dispose);

        foreach (var (index, fans) in FanControl)
        {
            steps.RunEssential($"GPU {index} fan release", fans.ReleaseForShutdown);
        }

        // File writes wait until the hardware is safe: a slow or failing disk
        // must not delay the fan release, let alone skip it.
        if (ActiveCsvLogger is { } csv)
        {
            steps.Run("CSV log", csv.Dispose);
        }

        if (!DemoMode)
        {
            foreach (var recorder in VfCurves.Values)
            {
                steps.Run("V/F curve save", () => recorder.Save());
            }
        }

        foreach (var flightRecorder in Flights.Values)
        {
            steps.Run("flight recorder", flightRecorder.Dispose);
        }

        if (Manager is { } manager)
        {
            steps.Run("GPU manager", manager.Dispose);
        }

        _hardwareReleased = steps.EssentialSucceeded;
        return steps.EssentialSucceeded;
    }
}
