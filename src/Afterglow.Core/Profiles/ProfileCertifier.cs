using System.Globalization;
using Afterglow.Core.Stress;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Profiles;

public sealed record CertifierOptions
{
    /// <summary>Test time per mode (transitions enforces a floor so several full cycles run).</summary>
    public int SecondsPerMode { get; init; } = 90;
}

public sealed record CertifierStatus(
    bool Running,
    string Phase,
    int ModeIndex,
    int ModeCount,
    TimeSpan ModeElapsed,
    TimeSpan ModeDuration,
    IReadOnlyList<string> Log,
    bool? Passed,
    string? FailedMode);

/// <summary>
/// Certification wizard: applies a saved profile, then runs all four stability
/// modes against it in sequence — sustained burn, transition cycling, boost
/// excursions, and the full-VRAM test. Every pass is stamped into the profile
/// (pinned to the tested offsets); passing all four marks the profile stable.
/// A failure stops the run and resets the GPU to driver defaults, because the
/// config just proved itself unsafe.
/// </summary>
public sealed class ProfileCertifier
{
    private readonly GpuTuner _tuner;
    private readonly ProfileStore _store;
    private readonly object _lock = new();
    private readonly List<string> _log = [];
    private Thread? _thread;
    private volatile bool _cancel;
    private CertifierStatus _status = new(false, "idle", 0, 4, TimeSpan.Zero, TimeSpan.Zero, [], null, null);

    public event Action<CertifierStatus>? StatusChanged;

    public ProfileCertifier(GpuTuner tuner, ProfileStore store, uint? pciBusId = null)
    {
        _tuner = tuner;
        _store = store;
        _pciBusId = pciBusId;
    }

    /// <summary>Binds the stress engines to the tuned card on multi-GPU systems.</summary>
    private readonly uint? _pciBusId;

    public CertifierStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    public bool IsRunning => _thread is { IsAlive: true } && !_cancel;

    public void Start(TuningProfile profile, CertifierOptions options)
    {
        if (IsRunning)
        {
            return;
        }

        _cancel = false;
        lock (_lock)
        {
            _log.Clear();
        }

        _thread = new Thread(() => Run(profile, options))
        {
            Name = "Afterglow certifier",
            IsBackground = true,
        };
        _thread.Start();
    }

    public void Cancel() => _cancel = true;

    private void Log(string line)
    {
        lock (_lock)
        {
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        }
    }

    private void Publish(
        bool running, string phase, int modeIndex, TimeSpan elapsed, TimeSpan duration,
        bool? passed = null, string? failedMode = null)
    {
        CertifierStatus status;
        lock (_lock)
        {
            status = new CertifierStatus(
                running, phase, modeIndex, CertificationModes.All.Count, elapsed, duration,
                _log.ToArray(), passed, failedMode);
            _status = status;
        }

        StatusChanged?.Invoke(status);
    }

    private void Run(TuningProfile profile, CertifierOptions options)
    {
        int seconds = Math.Clamp(options.SecondsPerMode, 30, 1800);
        Log($"Certifying '{profile.Name}' ({Fmt(profile.CoreOffsetMHz)} core, {Fmt(profile.MemOffsetMHz)} mem) — " +
            $"four modes, ~{seconds} s each.");
        Publish(true, "applying", 0, TimeSpan.Zero, TimeSpan.Zero);

        var applied = _tuner.Apply(profile);
        if (!applied.AllSucceeded)
        {
            Log($"Could not fully apply the profile: {applied.Summary}");
            Publish(false, "failed", 0, TimeSpan.Zero, TimeSpan.Zero, passed: false, failedMode: "apply");
            return;
        }

        Log("Profile applied.");

        for (int i = 0; i < CertificationModes.All.Count && !_cancel; i++)
        {
            string mode = CertificationModes.All[i];
            var duration = TimeSpan.FromSeconds(
                mode == CertificationModes.Transitions ? Math.Max(seconds, 90) : seconds);

            Log($"[{i + 1}/4] {ModeTitle(mode)} for {duration.TotalSeconds:F0} s…");
            (bool passed, string evidence, string? failDetail) = mode == CertificationModes.Vram
                ? RunVramMode(i, duration)
                : RunStressMode(mode, i, duration);

            if (_cancel)
            {
                Log("Cancelled. The profile stays applied; certifications earned so far are kept.");
                Publish(false, "cancelled", i, TimeSpan.Zero, TimeSpan.Zero);
                return;
            }

            if (!passed)
            {
                Log($"{ModeTitle(mode)} FAILED: {failDetail}");
                Log("Resetting to driver defaults — this configuration just proved unstable.");
                _ = _tuner.ResetToDefaults();
                Publish(false, "failed", i, TimeSpan.Zero, TimeSpan.Zero, passed: false, failedMode: mode);
                return;
            }

            Log($"{ModeTitle(mode)} passed ({evidence}).");
            StampCertification(profile, mode, (int)duration.TotalSeconds, evidence);
        }

        if (_cancel)
        {
            Publish(false, "cancelled", 0, TimeSpan.Zero, TimeSpan.Zero);
            return;
        }

        MarkStable(profile);
        Log($"'{profile.Name}' is certified across all four modes and marked stable. " +
            "It can now auto-apply at startup.");
        Publish(false, "done", CertificationModes.All.Count, TimeSpan.Zero, TimeSpan.Zero, passed: true);
    }

    private (bool Passed, string Evidence, string? FailDetail) RunStressMode(
        string mode, int modeIndex, TimeSpan duration)
    {
        var pattern = mode switch
        {
            CertificationModes.Transitions => StressPattern.Transitions,
            CertificationModes.Excursions => StressPattern.BoostExcursions,
            _ => StressPattern.Sustained,
        };

        using var stress = new GpuStressTest { Pattern = pattern, TargetPciBusId = _pciBusId };
        var done = new ManualResetEventSlim(false);
        StressProgress? terminal = null;

        stress.ProgressChanged += progress =>
        {
            Publish(true, mode, modeIndex, progress.Elapsed, duration);
            if (progress.State is StressState.ArtifactDetected or StressState.DeviceLost or StressState.Failed)
            {
                terminal = progress;
                done.Set();
            }
        };

        stress.Start();
        while (!done.Wait(TimeSpan.FromMilliseconds(500)))
        {
            if (_cancel || stress.Progress.Elapsed >= duration)
            {
                break;
            }
        }

        stress.StopAndWait(TimeSpan.FromSeconds(5));
        var final = terminal ?? stress.Progress;

        if (final.State is StressState.ArtifactDetected or StressState.DeviceLost or StressState.Failed)
        {
            return (false, string.Empty, final.Detail ?? final.State.ToString());
        }

        string evidence = pattern switch
        {
            StressPattern.Transitions => string.Create(
                CultureInfo.InvariantCulture,
                $"{final.Transitions} clock transitions, {final.TotalDispatches} dispatches, 0 errors"),
            StressPattern.BoostExcursions => string.Create(
                CultureInfo.InvariantCulture,
                $"{final.Transitions} boost excursions, 0 errors"),
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"{final.TotalDispatches} dispatches, 0 errors"),
        };
        return (true, evidence, null);
    }

    private (bool Passed, string Evidence, string? FailDetail) RunVramMode(int modeIndex, TimeSpan duration)
    {
        using var vram = new VramTest { TargetPciBusId = _pciBusId };
        vram.ProgressChanged += progress =>
            Publish(true, CertificationModes.Vram, modeIndex, progress.Elapsed, duration);
        vram.Start();

        // Run for the window, but insist on at least one complete round
        // (capped at 3× the window) so slow cards still get full coverage.
        var cap = duration * 3;
        while (!_cancel)
        {
            Thread.Sleep(500);
            var p = vram.Progress;
            if (p.State is StressState.ArtifactDetected or StressState.DeviceLost or StressState.Failed)
            {
                break;
            }

            if (p.Elapsed >= duration && p.Rounds >= 1)
            {
                break;
            }

            if (p.Elapsed >= cap)
            {
                break;
            }
        }

        vram.StopAndWait(TimeSpan.FromSeconds(10));
        var final = vram.Progress;

        if (final.State is StressState.ArtifactDetected or StressState.DeviceLost or StressState.Failed)
        {
            return (false, string.Empty, final.Detail ?? final.State.ToString());
        }

        if (final.Rounds < 1)
        {
            return (false, string.Empty,
                "The VRAM test did not complete a full coverage round in the allotted time.");
        }

        return (true, string.Create(
            CultureInfo.InvariantCulture,
            $"{final.PlannedBytes / (double)(1L << 30):F1} GiB × {final.Rounds} rounds, 0 errors"), null);
    }

    private void StampCertification(TuningProfile profile, string mode, int seconds, string evidence)
    {
        try
        {
            var stored = _store.Load(profile.Name) ?? profile;
            var kept = stored.Certifications
                .Where(c => !string.Equals(c.Mode, mode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            kept.Add(new ProfileCertification
            {
                Mode = mode,
                PassedAt = DateTimeOffset.Now,
                DurationSeconds = seconds,
                CoreOffsetMHz = profile.CoreOffsetMHz,
                MemOffsetMHz = profile.MemOffsetMHz,
                Evidence = evidence,
                DriverVersion = CertificationModes.CurrentDriverVersion,
            });
            _store.Save(stored with { Certifications = kept, ModifiedAt = DateTimeOffset.Now });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"Warning: could not persist the {mode} certification: {ex.Message}");
        }
    }

    private void MarkStable(TuningProfile profile)
    {
        try
        {
            var stored = _store.Load(profile.Name) ?? profile;
            _store.Save(stored with { MarkedStable = true, ModifiedAt = DateTimeOffset.Now });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"Warning: could not mark the profile stable: {ex.Message}");
        }
    }

    private static string ModeTitle(string mode) => mode switch
    {
        CertificationModes.Sustained => "Sustained burn",
        CertificationModes.Transitions => "Transition cycling",
        CertificationModes.Excursions => "Boost excursions",
        CertificationModes.Vram => "Full-VRAM test",
        _ => mode,
    };

    private static string Fmt(int offset) => offset >= 0 ? $"+{offset} MHz" : $"{offset} MHz";
}
