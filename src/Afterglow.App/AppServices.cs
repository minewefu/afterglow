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

    /// <summary>Measured voltage/frequency curve, fed continuously from telemetry.</summary>
    public required Core.Tuning.VfCurveRecorder VfCurve { get; init; }

    /// <summary>Always-on telemetry black box (null in demo mode).</summary>
    public Core.Diagnostics.FlightRecorder? Flight { get; init; }

    /// <summary>Postmortem of the previous session, when it ended in a crash.</summary>
    public Core.Diagnostics.CrashReport? LastCrashReport { get; init; }

    private AppSettings _settings = new();

    public AppSettings Settings => _settings;

    /// <summary>Mutates and persists settings; side effects (interval, rules) are applied.</summary>
    public void UpdateSettings(Func<AppSettings, AppSettings> mutate)
    {
        _settings = mutate(_settings);
        SettingsStore.Save(_settings);
        Telemetry.Interval = TimeSpan.FromMilliseconds(_settings.PollingIntervalMs);
        GameWatcher.UpdateRules(_settings.GameRules);
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

    public static AppServices Create(bool demoMode)
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
                VfCurve = demoCurve,
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

        var vfCurve = new Core.Tuning.VfCurveRecorder();
        vfCurve.Load();
        telemetry.SnapshotTaken += vfCurve.Add;

        // Analyze the previous flight recording BEFORE the new recorder
        // rotates it, then start this session's black box.
        var crashReport = Core.Diagnostics.CrashForensics.AnalyzePreviousSession(AppPaths.FlightDir);
        var flight = new Core.Diagnostics.FlightRecorder(AppPaths.FlightDir);
        int flightTick = 0;
        telemetry.SnapshotTaken += snapshot =>
        {
            if (snapshot.DeviceIndex != 0)
            {
                return;
            }

            flight.Record(snapshot);

            // Offsets change rarely; sample once a minute so CLI-applied tuning
            // is captured too (dedup happens inside the recorder).
            if (flightTick++ % 60 == 0 && manager.Gpus.Count > 0)
            {
                try
                {
                    var current = manager.Gpus[0].Tuner.ReadCurrent();
                    flight.RecordOffsets(current.CoreOffsetMHz, current.MemOffsetMHz);
                }
                catch (InvalidOperationException)
                {
                }
            }
        };

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
            VfCurve = vfCurve,
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
            VfCurve.Save();
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
