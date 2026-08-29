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

    /// <summary>Always-on telemetry black box (null in demo mode).</summary>
    public Core.Diagnostics.FlightRecorder? Flight { get; init; }

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

        AppPaths.EnsureCreated();

        // One curve per GPU, each fed only its own card's snapshots — a second
        // card's V/F points must never plan an undervolt for the first. The
        // primary GPU keeps the legacy vf-curve.json.
        var vfCurves = new Dictionary<uint, Core.Tuning.VfCurveRecorder>();
        foreach (var gpu in manager.Gpus)
        {
            var curveRecorder = new Core.Tuning.VfCurveRecorder
            {
                PersistPath = Core.Tuning.VfCurveRecorder.PathFor(
                    gpu.Uuid, isPrimary: gpu.Index == manager.Gpus[0].Index),
            };
            curveRecorder.Load();
            vfCurves[gpu.Index] = curveRecorder;
        }

        telemetry.SnapshotTaken += snapshot =>
        {
            if (vfCurves.TryGetValue(snapshot.DeviceIndex, out var curveRecorder))
            {
                curveRecorder.Add(snapshot);
            }
        };

        // Analyze the previous flight recording BEFORE the new recorder
        // rotates it, then start this session's black box. Only the resident
        // instance records (screenshot/demo runs pass enableBlackBox false —
        // they'd fight the resident instance over the file), and a recorder
        // failure degrades to "no black box" — diagnostics must never be the
        // reason the app can't start.
        Core.Diagnostics.CrashReport? crashReport = null;
        Core.Diagnostics.FlightRecorder? flight = null;
        if (enableBlackBox)
        {
            try
            {
                crashReport = Core.Diagnostics.CrashForensics.AnalyzePreviousSession(AppPaths.FlightDir);
                flight = new Core.Diagnostics.FlightRecorder(AppPaths.FlightDir);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Core.Diagnostics.Log.Info($"Flight recorder disabled for this session: {ex.Message}");
                flight = null;
            }
        }

        if (flight is { } recorder)
        {
            int flightTick = 0;
            telemetry.SnapshotTaken += snapshot =>
            {
                if (snapshot.DeviceIndex != 0)
                {
                    return;
                }

                recorder.Record(snapshot);

                // Offsets change rarely; sample once a minute so CLI-applied
                // tuning is captured too (dedup happens inside the recorder).
                if (flightTick++ % 60 == 0 && manager.Gpus.Count > 0)
                {
                    try
                    {
                        var current = manager.Gpus[0].Tuner.ReadCurrent();
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
            Flight = flight,
            LastCrashReport = crashReport,
        };
        services.InitializeSettings(SettingsStore.Load());
        services.GameWatcher.Start();
        _ = services.TdrWatchdog.Start();
        return services;
    }

    public void Dispose()
    {
        if (!DemoMode)
        {
            foreach (var recorder in VfCurves.Values)
            {
                recorder.Save();
            }
        }

        GameWatcher.Dispose();
        TdrWatchdog.Dispose();
        foreach (var fans in FanControl.Values)
        {
            fans.Dispose();
        }

        FrameMetrics.Dispose();
        ActiveCsvLogger?.Dispose();
        Telemetry.Dispose();
        Flight?.Dispose();
        Manager?.Dispose();
    }
}
