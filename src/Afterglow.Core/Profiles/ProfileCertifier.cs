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
    string? FailedMode,

    /// <summary>
    /// Whether the GPU was actually returned to driver defaults after a failure.
    /// <para>
    /// Null means no reset was attempted on this path — which is the case for an
    /// apply failure, where a partially applied profile is still on the card.
    /// The UI used to assert "the GPU was reset to driver defaults" for every
    /// failure, including that one, and the reset's own return value was
    /// discarded so a refused reset read exactly like a successful one.
    /// </para>
    /// </summary>
    bool? ResetSucceeded = null);

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
    private readonly IGpuTuner _tuner;
    private readonly ProfileStore _store;
    private readonly object _lock = new();
    private readonly List<string> _log = [];
    private Thread? _thread;
    private volatile bool _cancel;
    private CertifierStatus _status = new(false, "idle", 0, 4, TimeSpan.Zero, TimeSpan.Zero, [], null, null);

    public event Action<CertifierStatus>? StatusChanged;

    public ProfileCertifier(IGpuTuner tuner, ProfileStore store, uint? pciBusId = null,
        uint vendorId = Stress.StressAdapter.NvidiaVendorId)
    {
        _tuner = tuner;
        _store = store;
        _pciBusId = pciBusId;
        _vendorId = vendorId;
    }

    /// <summary>Binds the stress engines to the tuned card on multi-GPU systems.</summary>
    private readonly uint? _pciBusId;

    private readonly uint _vendorId;

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
        bool? passed = null, string? failedMode = null, bool? resetSucceeded = null)
    {
        CertifierStatus status;
        lock (_lock)
        {
            status = new CertifierStatus(
                running, phase, modeIndex, CertificationModes.All.Count, elapsed, duration,
                _log.ToArray(), passed, failedMode, resetSucceeded);
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

                // The reset's answer decides what may be claimed. Discarding it
                // let a driver that refused every knob — the usual state right
                // after the reset this failure implies — produce a UI line
                // asserting the GPU was back at defaults while it still held the
                // settings that had just proved unstable.
                var reset = _tuner.ResetToDefaults();
                if (!reset.AllSucceeded)
                {
                    Log($"The reset did NOT fully succeed ({reset.Summary}). This GPU may still be running the " +
                        "configuration that just failed — reset it from the Tuning page and verify.");
                }

                Publish(false, "failed", i, TimeSpan.Zero, TimeSpan.Zero, passed: false, failedMode: mode,
                    resetSucceeded: reset.AllSucceeded);
                return;
            }

            if (ProfileWasReset(profile) is string drift)
            {
                Log($"{ModeTitle(mode)} cannot be certified: {drift}.");
                Log("Nothing is stamped — the burn did not run against these settings.");
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

    /// <summary>
    /// A certification only means something if the profile was actually applied
    /// for the burn that earned it. Over the ~6 minutes of a four-mode run an
    /// automation rule, a TDR panic-reset, or another Afterglow surface can put
    /// the GPU back to stock, and nothing suppressed or noticed that — the
    /// remaining modes then passed against stock clocks and the profile was
    /// stamped stable on evidence gathered from settings it never ran at.
    /// <para>
    /// Deliberately narrow: it fires only when EVERY knob the profile asked for
    /// reads as stock, which is the signature of a reset. A single value that
    /// merely differs (the apply engine clamps to the driver's range) is not
    /// treated as drift, so a legitimately clamped profile is never failed.
    /// </para>
    /// </summary>
    private string? ProfileWasReset(TuningProfile profile)
    {
        var (core, mem, _, _, lockMHz) = _tuner.ReadCurrent();
        var caps = _tuner.Capabilities;

        // Witnesses must be knobs whose value is read back from the driver AND
        // whose stock value is unambiguous. Offsets qualify: 0 means stock.
        // The clock lock qualifies only where the driver actually reads it back
        // (IGCL does; NVML has no locked-clock getter, so there it is an
        // in-process shadow no external reset ever clears) — and on Intel the
        // clamp is the ONLY knob there is, so without it this check had nothing
        // to look at and was dead code on the one vendor verified on hardware.
        //
        // The power limit is deliberately NOT a witness even though both vendors
        // read it back: a reset restores it to the board default, and profiles
        // routinely carry that same default, so "back at stock" and "still
        // applied" are indistinguishable there. Using it would fail the first
        // mode of most certifications.
        // A clamp the profile never asked for is also disqualifying, in the
        // opposite direction: the burn would then run — and be stamped — under a
        // clock cap that is not part of what is being certified. Only checkable
        // where the driver reads the lock back.
        // One corroborating re-read serves both witnesses below: a third driver
        // round-trip per call bought nothing the second had not already read.
        (int Core, int Mem, uint? Lock)? secondRead = null;
        if (_tuner.LockIsDriverReadable && profile.LockedCoreClockMHz is null && lockMHz is not null)
        {
            // Corroborate, as the stock witness below does: one glitched range
            // read, or a race with another reader on the same tuner, must not
            // throw away a mode that just burned for 90 s.
            var (coreAgain, memAgain, _, _, lockAgain) = _tuner.ReadCurrent();
            secondRead = (coreAgain, memAgain, lockAgain);
            if (lockAgain is uint stray)
            {
                return $"the GPU is clamped to {stray} MHz, which this profile does not ask for — a burn under an " +
                    "unrequested clock cap cannot certify these settings";
            }
        }

        bool wantedCore = caps.SupportsCoreOffset && profile.CoreOffsetMHz != 0;
        bool wantedMem = caps.SupportsMemOffset && profile.MemOffsetMHz != 0;
        bool wantedLock = _tuner.LockIsDriverReadable && profile.LockedCoreClockMHz is not null;
        if (!wantedCore && !wantedMem && !wantedLock)
        {
            return null;
        }

        bool Stock(int c, int m, uint? l) =>
            (!wantedCore || c == 0) && (!wantedMem || m == 0) && (!wantedLock || l is null);

        bool allStock = Stock(core, mem, lockMHz);

        // A failed getter also reads back as 0/null, and aborting a certification
        // on one transient failure would discard every mode already earned.
        // Corroborate before calling it drift: a real reset stays reset.
        if (allStock)
        {
            var (core2, mem2, lock2) = secondRead ?? ReadAgain();
            allStock = Stock(core2, mem2, lock2);
        }

        (int, int, uint?) ReadAgain()
        {
            var (c, m, _, _, l) = _tuner.ReadCurrent();
            return (c, m, l);
        }

        return allStock
            ? "the GPU is back at stock, so the profile stopped being applied mid-run " +
              "(an automation rule, TDR recovery, or another Afterglow surface reset it)"
            : null;
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

        using var stress = new GpuStressTest
        {
            Pattern = pattern,
            TargetPciBusId = _pciBusId,
            TargetVendorId = _vendorId,
        };
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

        // A burn that never acknowledged its stop is NOT a pass. Its progress is
        // a stale mid-run snapshot, and any corruption that happened since the
        // last periodic verify was never checked — so there is no verdict to
        // stamp. Certification refuses rather than inventing a clean result.
        bool stoppedCleanly = stress.StopAndWait(TimeSpan.FromSeconds(30));
        var final = terminal ?? stress.Progress;

        if (final.State is StressState.ArtifactDetected or StressState.DeviceLost or StressState.Failed)
        {
            return (false, string.Empty, final.Detail ?? final.State.ToString());
        }

        if (!stoppedCleanly)
        {
            return (false, string.Empty,
                "The burn did not stop within 30 s, so its result is a stale mid-run snapshot with no " +
                "final verification — no stability verdict can be drawn from it.");
        }

        if (final.State is not StressState.Stopped)
        {
            return (false, string.Empty,
                $"The burn ended in an unexpected state ({final.State}) — no stability verdict can be drawn from it.");
        }

        // The engine's own evidence rule: load dispatches actually ran (the
        // total counts a one-off reference pass, so it is 1 even when the load
        // loop never did), and a cycling mode completed at least one cycle. The
        // transition mode used to be stamped on "0 clock transitions, 0 errors"
        // — a regime the run never entered.
        if (final.VerdictGap is { } gap)
        {
            return (false, string.Empty,
                $"{char.ToUpperInvariant(gap[0])}{gap[1..]} — no certification can be stamped from it.");
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
        using var vram = new VramTest
        {
            TargetPciBusId = _pciBusId,
            TargetVendorId = _vendorId,
        };
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

        bool stoppedCleanly = vram.StopAndWait(TimeSpan.FromSeconds(30));
        var final = vram.Progress;

        if (final.State is StressState.ArtifactDetected or StressState.DeviceLost or StressState.Failed)
        {
            return (false, string.Empty, final.Detail ?? final.State.ToString());
        }

        // Same rule as the burn: an abandoned run leaves a stale snapshot
        // behind, and a stale snapshot is not evidence of stability.
        if (!stoppedCleanly)
        {
            return (false, string.Empty,
                "The VRAM test did not stop within 30 s, so its result is a stale mid-run snapshot — " +
                "no stability verdict can be drawn from it.");
        }

        // Same terminal-state guard the burn twin got: only a run that reached
        // Stopped produced a result worth reading.
        if (final.State is not StressState.Stopped)
        {
            return (false, string.Empty,
                $"The VRAM test ended in an unexpected state ({final.State}) — no stability verdict can be drawn from it.");
        }

        if (final.Rounds < 1)
        {
            return (false, string.Empty,
                "The VRAM test did not complete a full coverage round in the allotted time.");
        }

        // A UMA pass must not be stamped with dedicated-VRAM meaning: the
        // engine's honest note (null on dedicated-VRAM cards, so NVIDIA
        // evidence is byte-identical) rides into the certification evidence.
        string evidence = string.Create(
            CultureInfo.InvariantCulture,
            $"{final.PlannedBytes / (double)(1L << 30):F1} GiB × {final.Rounds} rounds, 0 errors");
        if (final.Detail is { Length: > 0 } note)
        {
            evidence += $" — {note}";
        }

        return (true, evidence, null);
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
                GpuUuid = _tuner.GpuUuid,
                DriverVersion = CertificationModes.CurrentDriverFor(_tuner.GpuUuid),
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
