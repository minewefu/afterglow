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
/// Certification wizard: applies a saved profile — its fan policy, then its
/// tuning — and runs all four stability modes against it in sequence: sustained
/// burn, transition cycling, boost excursions, and the full-VRAM test. Every
/// pass is stamped into the profile, pinned to the fingerprint of the tested
/// configuration; passing all four marks the profile stable. A demonstrated
/// failure (an artifact or a driver reset) revokes that configuration's stable
/// mark and the failed mode's certificate, then resets the GPU to driver
/// defaults, because the config just proved itself unsafe.
/// <para>
/// Phases a run can end in: "done", "cancelled", "failed" (with
/// <see cref="CertifierStatus.FailedMode"/> naming the mode, or "apply"/"fans"
/// when the profile could not be put on the card), and "interrupted" — another
/// writer changed the configuration under test, or the saved profile was
/// replaced, mid-run (R-02, R-03). An interrupted run is never a pass and writes
/// nothing more to the GPU's tuning.
/// </para>
/// </summary>
public sealed class ProfileCertifier
{
    private readonly IGpuTuner _tuner;
    private readonly ProfileStore _store;
    private readonly CertificationFans? _fans;
    private readonly Lock _lock = new();
    private readonly List<string> _log = [];
    private Thread? _thread;
    private volatile bool _cancel;
    private CertifierStatus _status = new(false, "idle", 0, 4, TimeSpan.Zero, TimeSpan.Zero, [], null, null);

    public event Action<CertifierStatus>? StatusChanged;

    /// <param name="fans">
    /// The card's fan control, so the run happens under the profile's own fan
    /// policy and the previous one is put back afterwards (R-11). Without it a
    /// profile with a fixed or curve fan policy is refused rather than burned
    /// under someone else's fans; an auto-fan profile runs with the fans left as
    /// they are, and the log says so.
    /// </param>
    public ProfileCertifier(IGpuTuner tuner, ProfileStore store, uint? pciBusId = null,
        uint vendorId = Stress.StressAdapter.NvidiaVendorId, CertificationFans? fans = null)
    {
        _tuner = tuner;
        _store = store;
        _pciBusId = pciBusId;
        _vendorId = vendorId;
        _fans = fans;
    }

    /// <summary>Binds the stress engines to the tuned card on multi-GPU systems.</summary>
    private readonly uint? _pciBusId;

    private readonly uint _vendorId;

    /// <summary>
    /// The burn engine for the three stress modes. Null means the real burn
    /// (<see cref="GpuStressTest"/>); tests supply a load that runs nothing.
    /// </summary>
    internal Func<StressPattern, IProbeLoad>? StressFactory { get; set; }

    /// <summary>The full-VRAM engine. Null means the real <see cref="VramTest"/>.</summary>
    internal Func<ICertifierVramLoad>? VramFactory { get; set; }

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

    /// <summary>How a run ended; published once, after the fans have been put back.</summary>
    private readonly record struct Outcome(
        string Phase, int ModeIndex, bool? Passed = null, string? FailedMode = null, bool? ResetSucceeded = null);

    /// <summary>
    /// One mode's result. <see cref="HardwareFailure"/> separates a demonstrated
    /// fault — an artifact or a driver reset — from a run that produced no
    /// verdict at all (no adapter, an unclean stop, no load dispatched): only the
    /// first is evidence against the configuration (R-04).
    /// </summary>
    private readonly record struct ModeResult(bool Passed, string Evidence, string? FailDetail, bool HardwareFailure)
    {
        public static ModeResult Pass(string evidence) => new(true, evidence, null, false);

        public static ModeResult NoVerdict(string detail) => new(false, string.Empty, detail, false);

        public static ModeResult Fault(string detail) => new(false, string.Empty, detail, true);
    }

    private void Run(TuningProfile profile, CertifierOptions options)
    {
        int seconds = Math.Clamp(options.SecondsPerMode, 30, 1800);

        // What this run tests, as a fingerprint (R-03): every write to the store
        // checks it is still writing about this configuration and not merely a
        // profile with the same name.
        string fingerprint = profile.TuningFingerprint();
        Log($"Certifying '{profile.Name}' ({Fmt(profile.CoreOffsetMHz)} core, {Fmt(profile.MemOffsetMHz)} mem, " +
            $"fans: {CertificationFans.Describe(profile)}) — four modes, ~{seconds} s each.");
        Publish(true, "applying", 0, TimeSpan.Zero, TimeSpan.Zero);

        Outcome outcome;
        using (var guard = new TuningRunGuard(_tuner))
        {
            outcome = Certify(profile, fingerprint, seconds, guard);
        }

        // Every exit puts the fans back the way the run found them (R-11) —
        // unless another control took them over mid-run, which is newer than us.
        if (_fans?.Restore() is string fans)
        {
            Log(fans);
        }

        Publish(false, outcome.Phase, outcome.ModeIndex, TimeSpan.Zero, TimeSpan.Zero,
            outcome.Passed, outcome.FailedMode, outcome.ResetSucceeded);
    }

    private Outcome Certify(TuningProfile profile, string fingerprint, int seconds, TuningRunGuard guard)
    {
        // The subject is the configuration handed in. A saved profile of that
        // name that already differs could carry nothing this run proves.
        var saved = _store.Load(profile.Name);
        if (saved is null || !IsSameConfiguration(saved, fingerprint))
        {
            Log($"Not started: the saved profile '{profile.Name}' " +
                (saved is null ? "cannot be read" : "differs from the configuration handed to certification") +
                ". Nothing was applied — reload the profile and certify it again.");
            return new Outcome("interrupted", 0, Passed: false);
        }

        // The fan policy is part of the configuration under test (R-11), and it
        // goes on first, so a policy the driver refuses leaves the clocks alone.
        if (ApplyFans(profile) is string fanRefusal)
        {
            Log($"Could not apply the profile's fan policy ({CertificationFans.Describe(profile)}): {fanRefusal}. " +
                "Its clocks were not applied — certifying them under other fans would test a different thermal " +
                "configuration from the one the profile runs with.");
            return new Outcome("failed", 0, Passed: false, FailedMode: "fans");
        }

        var applied = guard.Own(() => _tuner.Apply(profile));
        if (!applied.AllSucceeded)
        {
            Log($"Could not fully apply the profile: {applied.Summary}");
            return new Outcome("failed", 0, Passed: false, FailedMode: "apply");
        }

        Log("Profile applied.");

        // What every mode must still find on the card afterwards: the CLAMPED
        // values the driver accepted, read back — not the profile's requests,
        // and not merely "anything but stock" (R-02).
        var onCard = guard.CaptureApplied();

        // A clamp the profile never asked for is disqualifying in the opposite
        // direction: the burn would run — and be stamped — under a clock cap
        // that is not part of what is being certified. Only checkable where the
        // driver reads the lock back; refused before the first 90 s burn rather
        // than after it.
        if (_tuner.LockIsDriverReadable && profile.LockedCoreClockMHz is null && onCard.LockedCoreClockMHz is uint stray)
        {
            Log($"Cannot certify: the GPU is clamped to {stray} MHz, which this profile does not ask for — a burn " +
                "under an unrequested clock cap cannot certify these settings. Release it on the Tuning page and retry.");
            return new Outcome("failed", 0, Passed: false, FailedMode: "apply");
        }

        for (int i = 0; i < CertificationModes.All.Count && !_cancel; i++)
        {
            string mode = CertificationModes.All[i];
            var duration = TimeSpan.FromSeconds(
                mode == CertificationModes.Transitions ? Math.Max(seconds, 90) : seconds);

            Log($"[{i + 1}/4] {ModeTitle(mode)} for {duration.TotalSeconds:F0} s…");
            var result = mode == CertificationModes.Vram
                ? RunVramMode(i, duration, guard)
                : RunStressMode(mode, i, duration, guard);

            // A demonstrated fault decides the outcome before anything else,
            // cancel and interruption included (R-04): the hardware misbehaved,
            // and an approval that outlives that is how settings the app just
            // watched fail get auto-applied at the next startup.
            if (result.HardwareFailure)
            {
                return DemonstratedFailure(profile, fingerprint, mode, i, result.FailDetail, guard);
            }

            if (guard.Interrupted)
            {
                return Interrupted(i,
                    $"another tuning write reached this GPU while the {ModeTitle(mode)} mode was running — the " +
                    "Tuning page, a profile hotkey, a game rule, automation or a reset");
            }

            if (_cancel)
            {
                Log("Cancelled. The profile stays applied; certifications earned so far are kept.");
                return new Outcome("cancelled", i);
            }

            if (!result.Passed)
            {
                Log($"{ModeTitle(mode)} FAILED: {result.FailDetail}");
                Log("Resetting to driver defaults — this mode produced no verdict, so its settings are not left " +
                    "applied unverified. Earlier approvals are unaffected: nothing was demonstrated against them.");
                return ResetAfterFailure(mode, i, guard);
            }

            // Changes no in-process event reports: another process, the driver
            // itself, or a different control taking over the fans.
            if ((guard.Drift() ?? _fans?.Drift()) is string drift)
            {
                return Interrupted(i, drift);
            }

            Log($"{ModeTitle(mode)} passed ({result.Evidence}).");
            if (StampCertification(profile, fingerprint, mode, (int)duration.TotalSeconds, result.Evidence) is string lost)
            {
                return SubjectReplaced(i, lost);
            }
        }

        if (_cancel)
        {
            return new Outcome("cancelled", 0);
        }

        var (marked, replaced) = MarkStable(profile, fingerprint);
        if (replaced is not null)
        {
            return SubjectReplaced(CertificationModes.All.Count, replaced);
        }

        Log(marked
            ? $"'{profile.Name}' is certified across all four modes and marked stable. It can now auto-apply at startup."
            : $"'{profile.Name}' passed all four modes, but the stable mark could not be saved, so it will not " +
              "auto-apply at startup.");
        return new Outcome("done", CertificationModes.All.Count, Passed: true);
    }

    /// <summary>
    /// The profile's fan policy onto the card, or why it could not go on (R-11).
    /// Without fan control an auto-fan profile still runs — the log says the fans
    /// were left alone — but a fixed or curve policy is refused.
    /// </summary>
    private string? ApplyFans(TuningProfile profile)
    {
        if (_fans is not null)
        {
            if (_fans.Apply(profile) is string refusal)
            {
                return refusal;
            }

            Log($"Fan policy applied: {CertificationFans.Describe(profile)}.");
            return null;
        }

        if (profile.FanMode != FanMode.Auto)
        {
            return "this run has no fan control for this GPU";
        }

        Log("This run does not control the fans; they stay as they are.");
        return null;
    }

    /// <summary>
    /// Ends a run whose configuration changed under it (R-02). Nothing is written
    /// to the tuning afterwards — no reset, no re-apply: whoever wrote last owns
    /// the card now, and overwriting them would undo a deliberate act (a reset,
    /// a hotkey, a game profile). Passes stamped before the change were checked
    /// against the card at the end of their own mode, so they stand.
    /// </summary>
    private Outcome Interrupted(int modeIndex, string why)
    {
        Log($"Interrupted: {why}.");
        Log("This mode is not stamped, the profile is not marked stable, and nothing more is written to the GPU's " +
            "tuning — it stays as that change left it. Certifications earned before it are kept.");
        return new Outcome("interrupted", modeIndex, Passed: false);
    }

    /// <summary>The saved profile stopped being the configuration under test (R-03).</summary>
    private Outcome SubjectReplaced(int modeIndex, string why)
    {
        Log($"Interrupted: {why}. Nothing more is stamped on it and it is not marked stable — certify the saved " +
            "version again.");
        return new Outcome("interrupted", modeIndex, Passed: false);
    }

    private Outcome DemonstratedFailure(
        TuningProfile profile, string fingerprint, string mode, int modeIndex, string? detail, TuningRunGuard guard)
    {
        Log($"{ModeTitle(mode)} FAILED: {detail}");
        if (guard.Interrupted)
        {
            // A fault observed while someone else also wrote cannot be pinned on
            // one configuration for certain. Revoking on doubtful evidence costs
            // a re-run; keeping an approval through a fault costs a crash.
            Log("Another tuning write also reached this GPU during this mode; the fault still counts against this " +
                "configuration.");
        }

        RevokeApproval(profile, fingerprint, mode);
        Log("Resetting to driver defaults — this configuration just proved unstable.");
        return ResetAfterFailure(mode, modeIndex, guard);
    }

    private Outcome ResetAfterFailure(string mode, int modeIndex, TuningRunGuard guard)
    {
        // The reset's answer decides what may be claimed. Discarding it let a
        // driver that refused every knob — the usual state right after the reset
        // a failure implies — produce a UI line asserting the GPU was back at
        // defaults while it still held the settings that had just proved unstable.
        var reset = guard.Own(() => _tuner.ResetToDefaults());
        if (!reset.AllSucceeded)
        {
            Log($"The reset did NOT fully succeed ({reset.Summary}). This GPU may still be running the " +
                "configuration that just failed — reset it from the Tuning page and verify.");
        }

        return new Outcome("failed", modeIndex, Passed: false, FailedMode: mode, ResetSucceeded: reset.AllSucceeded);
    }

    private ModeResult RunStressMode(string mode, int modeIndex, TimeSpan duration, TuningRunGuard guard)
    {
        var pattern = mode switch
        {
            CertificationModes.Transitions => StressPattern.Transitions,
            CertificationModes.Excursions => StressPattern.BoostExcursions,
            _ => StressPattern.Sustained,
        };

        using IProbeLoad stress = StressFactory?.Invoke(pattern) ?? new GpuStressTest
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
            // Another writer ends the burn within a tick: whatever it goes on
            // to measure is no longer about this configuration.
            if (_cancel || guard.Interrupted || stress.Progress.Elapsed >= duration)
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

        if (final.IsHardwareVerdict)
        {
            return ModeResult.Fault(final.Detail ?? final.State.ToString());
        }

        if (final.State is StressState.Failed)
        {
            return ModeResult.NoVerdict(final.Detail ?? final.State.ToString());
        }

        if (!stoppedCleanly)
        {
            return ModeResult.NoVerdict(
                "The burn did not stop within 30 s, so its result is a stale mid-run snapshot with no " +
                "final verification — no stability verdict can be drawn from it.");
        }

        if (final.State is not StressState.Stopped)
        {
            return ModeResult.NoVerdict(
                $"The burn ended in an unexpected state ({final.State}) — no stability verdict can be drawn from it.");
        }

        // The engine's own evidence rule: load dispatches actually ran (the
        // total counts a one-off reference pass, so it is 1 even when the load
        // loop never did), and a cycling mode completed at least one cycle. The
        // transition mode used to be stamped on "0 clock transitions, 0 errors"
        // — a regime the run never entered.
        if (final.VerdictGap is { } gap)
        {
            return ModeResult.NoVerdict(
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
        return ModeResult.Pass(evidence);
    }

    private ModeResult RunVramMode(int modeIndex, TimeSpan duration, TuningRunGuard guard)
    {
        using ICertifierVramLoad vram = VramFactory?.Invoke() ?? new VramTestLoad(new VramTest
        {
            TargetPciBusId = _pciBusId,
            TargetVendorId = _vendorId,
        });
        vram.ProgressChanged += progress =>
            Publish(true, CertificationModes.Vram, modeIndex, progress.Elapsed, duration);
        vram.Start();

        // Run for the window, but insist on at least one complete round
        // (capped at 3× the window) so slow cards still get full coverage.
        var cap = duration * 3;
        while (!_cancel && !guard.Interrupted)
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

        if (final.IsHardwareVerdict)
        {
            return ModeResult.Fault(final.Detail ?? final.State.ToString());
        }

        if (final.State is StressState.Failed)
        {
            return ModeResult.NoVerdict(final.Detail ?? final.State.ToString());
        }

        // Same rule as the burn: an abandoned run leaves a stale snapshot
        // behind, and a stale snapshot is not evidence of stability.
        if (!stoppedCleanly)
        {
            return ModeResult.NoVerdict(
                "The VRAM test did not stop within 30 s, so its result is a stale mid-run snapshot — " +
                "no stability verdict can be drawn from it.");
        }

        // Same terminal-state guard the burn twin got: only a run that reached
        // Stopped produced a result worth reading.
        if (final.State is not StressState.Stopped)
        {
            return ModeResult.NoVerdict(
                $"The VRAM test ended in an unexpected state ({final.State}) — no stability verdict can be drawn from it.");
        }

        if (final.Rounds < 1)
        {
            return ModeResult.NoVerdict("The VRAM test did not complete a full coverage round in the allotted time.");
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

        return ModeResult.Pass(evidence);
    }

    private static bool IsSameConfiguration(TuningProfile stored, string fingerprint) =>
        string.Equals(stored.TuningFingerprint(), fingerprint, StringComparison.Ordinal);

    /// <summary>Why the saved profile is no longer the configuration under test, or null when it still is.</summary>
    private static string? Unbound(TuningProfile? stored, string name, string fingerprint) =>
        stored is null
            ? $"the saved profile '{name}' can no longer be read (deleted, renamed or unreadable)"
            : IsSameConfiguration(stored, fingerprint)
                ? null
                : $"the saved profile '{name}' was changed since this run started, so it is no longer the " +
                  "configuration under test";

    /// <summary>
    /// Stamps a passed mode into the saved profile — only while that saved
    /// profile is still the configuration under test (R-03). Reloading by name
    /// alone stamped whatever had been saved under the name since: new sliders,
    /// never applied, never burned. Returns why it no longer is; a failure to
    /// persist is logged and the run goes on, as before.
    /// </summary>
    private string? StampCertification(TuningProfile tested, string fingerprint, string mode, int seconds, string evidence)
    {
        try
        {
            var stored = _store.Load(tested.Name);
            if (Unbound(stored, tested.Name, fingerprint) is string unbound)
            {
                return unbound;
            }

            var kept = stored!.Certifications
                .Where(c => !string.Equals(c.Mode, mode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            kept.Add(new ProfileCertification
            {
                Mode = mode,
                PassedAt = DateTimeOffset.Now,
                DurationSeconds = seconds,
                CoreOffsetMHz = tested.CoreOffsetMHz,
                MemOffsetMHz = tested.MemOffsetMHz,
                Evidence = evidence,
                GpuUuid = _tuner.GpuUuid,
                DriverVersion = CertificationModes.CurrentDriverFor(_tuner.GpuUuid),
                TuningFingerprint = fingerprint,
            });
            _store.Save(stored with { Certifications = kept, ModifiedAt = DateTimeOffset.Now });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"Warning: could not persist the {mode} certification: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Marks the saved profile stable — only while it is still the configuration
    /// that passed (R-03). Startup auto-applies a stable profile, so this is the
    /// one write that must never land on a same-name replacement.
    /// </summary>
    private (bool Marked, string? Unbound) MarkStable(TuningProfile tested, string fingerprint)
    {
        try
        {
            var stored = _store.Load(tested.Name);
            if (Unbound(stored, tested.Name, fingerprint) is string unbound)
            {
                return (false, unbound);
            }

            _store.Save(stored! with { MarkedStable = true, ModifiedAt = DateTimeOffset.Now });
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"Warning: could not mark the profile stable: {ex.Message}");
            return (false, null);
        }
    }

    /// <summary>
    /// After a demonstrated failure: clears the stable mark and the failed
    /// mode's certificate for the configuration that just failed (R-04). The
    /// failure path used to reset the card and return, leaving an earlier
    /// certification and MarkedStable in place — so startup could auto-apply
    /// the very settings this run had watched fail.
    /// <para>
    /// The stable mark is cleared only when the saved profile IS the tested
    /// configuration; a replacement saved mid-run is a different configuration
    /// and nothing here speaks for it. Certificates of the failed mode that
    /// vouch for the tested configuration go either way. The other modes'
    /// certificates stay: those passes happened, they simply no longer add up
    /// to a certified profile.
    /// </para>
    /// </summary>
    private void RevokeApproval(TuningProfile tested, string fingerprint, string mode)
    {
        try
        {
            if (_store.Load(tested.Name) is not { } stored)
            {
                return;
            }

            var kept = stored.Certifications
                .Where(c => !(string.Equals(c.Mode, mode, StringComparison.OrdinalIgnoreCase) &&
                              c.MatchesConfigurationOf(tested)))
                .ToList();
            bool unmark = stored.MarkedStable && IsSameConfiguration(stored, fingerprint);
            int revoked = stored.Certifications.Count - kept.Count;
            if (!unmark && revoked == 0)
            {
                return;
            }

            _store.Save(stored with
            {
                MarkedStable = stored.MarkedStable && !unmark,
                Certifications = kept,
                ModifiedAt = DateTimeOffset.Now,
            });

            var what = new List<string>();
            if (unmark)
            {
                what.Add("its stable mark (it will no longer auto-apply at startup)");
            }

            if (revoked > 0)
            {
                what.Add($"its '{mode}' certification");
            }

            Log($"Revoked {string.Join(" and ", what)}: '{tested.Name}' just failed at this configuration.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"WARNING: could not revoke '{tested.Name}''s approval after this failure ({ex.Message}). It may " +
                "still be marked stable and auto-apply at startup — clear that on the Profiles page.");
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

    /// <summary>The real VRAM engine behind <see cref="ICertifierVramLoad"/>.</summary>
    private sealed class VramTestLoad(VramTest test) : ICertifierVramLoad
    {
        public event Action<VramProgress>? ProgressChanged
        {
            add => test.ProgressChanged += value;
            remove => test.ProgressChanged -= value;
        }

        public VramProgress Progress => test.Progress;

        public void Start() => test.Start();

        public bool StopAndWait(TimeSpan timeout) => test.StopAndWait(timeout);

        public void Dispose() => test.Dispose();
    }
}

/// <summary>
/// The full-VRAM engine as the certifier drives it — the twin of
/// <see cref="IProbeLoad"/> for <see cref="VramTest"/>, so a certification run
/// can be exercised end to end without a D3D device.
/// </summary>
internal interface ICertifierVramLoad : IDisposable
{
    event Action<VramProgress>? ProgressChanged;

    VramProgress Progress { get; }

    void Start();

    /// <summary>False when the worker was still running at the timeout.</summary>
    bool StopAndWait(TimeSpan timeout);
}
