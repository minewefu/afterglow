using Afterglow.Core.Diagnostics;
using Afterglow.Core.Interop.Igcl;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;

namespace Afterglow.Core.Tuning;

/// <summary>
/// Intel Arc tuner over IGCL. A capability flag here means "Afterglow drives
/// this knob on this device, verified by readback" — every one is probed live
/// at construction, never assumed by generation:
///
/// - Locked core clock maps to the GPU frequency-domain range clamp
///   (ctlFrequencySetRange), the one GPU-domain control the verified
///   OneXPlayer 3 driver reports as controllable. Unlike NVML's lock, IGCL
///   has a real readback getter, so clamp applies and releases verify or
///   fail loudly.
/// - Power limit maps to ctlOverclockPowerLimitSetV2 and lights up only where
///   the driver's own capability block (ctlOverclockGetProperties) reports it
///   supported — false on the verified iGPU, expected true on discrete Arc
///   (implemented against the documented API; awaiting field confirmation).
/// - Offsets, voltage, temp limit, V/F points, fans: not implemented; the
///   capability report keeps them false and every surface renders its honest
///   "not supported" state.
/// </summary>
public sealed class ArcGpuTuner : IGpuTuner
{
    private readonly IArcDevice _device;
    private readonly nint _gpuFreqDomain;
    private readonly double _hwMinMhz;
    private readonly double _hwMaxMhz;
    private readonly bool _powerLimitInMilliwatts;
    private readonly object _applyLock = new();

    private bool _waiverSigned;

    /// <summary>
    /// The clamp this tuner believes is on the card, and the lock Afterglow
    /// applied that it stands for: the clamp's own value for a range lock this
    /// process wrote or inherited from a crashed session's record, null for a
    /// ceiling ReadCurrent merely observed or for the probe's exact pin. One
    /// value, so nothing has to be reset in step by hand.
    /// </summary>
    private readonly record struct TrackedClamp(uint Mhz, uint? AppliedLock);

    private TrackedClamp? _clamp;

    /// <summary>
    /// The unclamped ceiling, once a release has verified it; null until then.
    /// The header warns that the -1 "factory value" restore can land below the
    /// hardware max, so the ceiling clamp detection compares against is a
    /// measurement where one exists and the domain maximum otherwise (see
    /// <see cref="ReleasedMaxMhz"/>). A narrower range seen at startup is NOT
    /// adopted: it is either a factory limit or a clamp that outlived a crashed
    /// session, and adopting it made a real leftover clamp permanently
    /// invisible — a GPU still pinned at 450 MHz reported "no clock lock".
    /// </summary>
    private double? _observedReleasedMaxMhz;

    private double ReleasedMaxMhz => _observedReleasedMaxMhz ?? _hwMaxMhz;

    /// <summary>
    /// Between <see cref="BeginProbe"/> and a successful <see cref="EndProbe"/>:
    /// the range lock the sweep displaced. Kept in the tuner because only the
    /// tuner sees it before the pre-sweep release empties the tracking; while
    /// it is set, <see cref="AppliedLockMHz"/> answers with it, so a failed pin
    /// release does not make the user's lock vanish until the next restart.
    /// </summary>
    private uint? _probeDisplacedLock;

    /// <summary>A pin write of the current sweep landed with the driver, so EndProbe has something to release.</summary>
    private bool _probePinLanded;

    /// <summary>The highest clock a sweep may pin: the verified released ceiling, or the domain maximum until one is known.</summary>
    public uint MaxLockableClockMHz =>
        _observedReleasedMaxMhz is double known && known > 0 ? (uint)Math.Round(known) : Capabilities.MaxCoreClockMHz;

    /// <summary>
    /// A release landed: the readback ceiling becomes the released baseline,
    /// the tracked clamp (and any displaced lock a sweep remembered) is
    /// cleared, and any V/F probe record for this card is resolved — the floor
    /// gate before every call here proves no pin survives.
    /// </summary>
    private void AdoptReleasedBaseline(double max)
    {
        _clamp = null;
        _probeDisplacedLock = null;
        _observedReleasedMaxMhz = max;
        ResolveProbeRecord();
    }

    /// <summary>See <see cref="IGpuTuner.RecordKey"/>.</summary>
    private void ResolveProbeRecord() => AppliedStateStore.ResolveProbeLock(RecordKey);

    public ArcGpuTuner(IArcDevice device, string gpuUuid)
    {
        _device = device;
        GpuUuid = gpuUuid;
        RecordKey = gpuUuid; // Intel identities are always derived, never missing

        bool supportsClamp = false;
        foreach (var (handle, props) in device.GetFrequencyDomains())
        {
            if (props.Type != CtlFreqDomain.Gpu || _gpuFreqDomain != 0)
            {
                continue;
            }

            _gpuFreqDomain = handle;
            _hwMinMhz = props.Min;
            _hwMaxMhz = props.Max;

            // Controllable only if the driver says so AND the readback getter
            // answers — the clamp ships with write-then-verify or not at all.
            supportsClamp = props.CanControl != 0 && props.Max > 0
                && _device.TryGetFrequencyRange(handle, out _) == CtlResult.Success;
        }

        bool supportsPowerLimit = false;
        double plMinW = 0, plMaxW = 0, plDefaultW = 0;
        if (device.TryGetOcProperties(out var oc) == CtlResult.Success
            && oc.Supported != 0 && oc.PowerLimit.Supported != 0)
        {
            // V1-era drivers report mW, Arc-era report per the units field.
            _powerLimitInMilliwatts = oc.PowerLimit.Units == CtlUnits.PowerMilliwatts;
            double toW = _powerLimitInMilliwatts ? 0.001 : 1.0;
            plMinW = oc.PowerLimit.Min * toW;
            plMaxW = oc.PowerLimit.Max * toW;
            plDefaultW = oc.PowerLimit.Default * toW;

            // The capability block alone isn't enough: the write path uses the
            // V2 entry points, which older runtimes may not export. Same rule
            // as the clamp — the knob ships with its readback getter or not
            // at all.
            supportsPowerLimit = plMaxW > 0
                && device.TryGetOcPowerLimitV2(out _) == CtlResult.Success;
        }

        Capabilities = new TuningCapabilities
        {
            SupportsLockedCoreClock = supportsClamp,
            MaxCoreClockMHz = supportsClamp ? (uint)Math.Round(_hwMaxMhz) : 0,
            LockClockMinMHz = supportsClamp ? (uint)Math.Round(_hwMinMhz) : 0,
            SupportsPowerLimit = supportsPowerLimit,
            PowerLimitMinW = plMinW,
            PowerLimitMaxW = plMaxW,
            PowerLimitDefaultW = plDefaultW,
        };

        // Adopt a tracked clamp only from this card's own record (an unstamped
        // legacy record never belongs to an Intel identity; see
        // AppliedStateStore.LoadOrAdoptLegacy).
        if (AppliedStateStore.LoadOrAdoptLegacy(RecordKey, gpuUuid) is { LockedCoreClockMHz: uint tracked })
        {
            _clamp = new TrackedClamp(tracked, tracked); // Afterglow wrote it: never a factory value
        }
    }

    public TuningCapabilities Capabilities { get; }

    public string? GpuUuid { get; }

    /// <inheritdoc />
    public string RecordKey { get; }

    // Locked like GpuTuner's: Nullable<uint> reads are not atomic, and probe
    // restore paths capture this value from other threads.
    //
    // The lock Afterglow APPLIED — never a ceiling merely observed. The probe
    // restores this value at the end of a sweep, and restoring an observed
    // factory ceiling wrote it back through the verified-write path with
    // "written" provenance, which blocked the adoption path forever.
    public uint? AppliedLockMHz
    {
        get
        {
            lock (_applyLock)
            {
                // A probe's exact pin is written by this process, but it is
                // the probe's, never the lock Afterglow applied: reporting it
                // here made the next sweep restore a failed-release pin as a
                // range lock the user never set, on every stepper step too.
                return _probeDisplacedLock ?? _clamp?.AppliedLock;
            }
        }
    }

    /// <summary>
    /// IGCL exposes a real frequency-range getter, so the clamp ReadCurrent
    /// reports is read back from the driver, not remembered.
    /// </summary>
    public bool LockIsDriverReadable => true;

    /// <summary>The IGCL device, for later milestones' write paths.</summary>
    public IArcDevice Device => _device;

    public (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz) ReadCurrent()
    {
        double? powerW = null;
        if (Capabilities.SupportsPowerLimit
            && _device.TryGetOcPowerLimitV2(out double raw) == CtlResult.Success && raw > 0)
        {
            powerW = _powerLimitInMilliwatts ? raw / 1000.0 : raw;
        }

        // The clamp has a real getter — when it answers, the driver's value
        // fully determines what is reported, INCLUDING "released": a stale
        // tracked shadow (say, from applied state persisted before a reboot
        // cleared the clamp) must never override a live released answer. The
        // shadow is only the fallback for a failed read.
        // The driver query and the commit of what it said must be one atomic
        // step against Apply, which holds this same lock across its own writes.
        // Reading the range outside the lock let an Apply land in between, so a
        // just-applied clamp was overwritten with the pre-apply answer and the
        // clamp went missing from every later read.
        uint? lockMHz = RefreshTrackedClampFromDriver();

        return (0, 0, powerW, null, lockMHz);
    }

    /// <summary>
    /// Reads the live frequency range under the apply lock and commits what it
    /// shows to the shadow; returns the clamp the driver reports, or the shadow
    /// when the getter does not answer. The one place the range is
    /// interpreted — ReadCurrent and the lock-less apply path both use it, so
    /// the two cannot drift apart on tolerances.
    /// <para>
    /// Provenance: a released reading clears everything; a reading that differs
    /// from the tracked value (beyond the write paths' 1 MHz readback tolerance
    /// plus the rounding on both sides, so 2 MHz) is an
    /// OBSERVATION — whatever the old value's provenance was does not transfer.
    /// A record inherited from a crashed session that a reboot has since
    /// cleared must not pin its "inherited" flag onto the factory ceiling the
    /// driver reports now, or that ceiling could never be adopted.
    /// </para>
    /// </summary>
    private uint? RefreshTrackedClampFromDriver()
    {
        lock (_applyLock)
        {
            if (_gpuFreqDomain == 0
                || _device.TryGetFrequencyRange(_gpuFreqDomain, out var range) != CtlResult.Success)
            {
                return _clamp?.Mhz;
            }

            bool maxClamped = range.Max > 0 && range.Max < ReleasedMaxMhz - 0.5;
            bool minRaised = range.Min > 0 && range.Min > _hwMinMhz + 0.5;
            uint? lockMHz = maxClamped ? (uint)Math.Round(range.Max)
                : minRaised ? (uint)Math.Round(range.Max > 0 ? range.Max : _hwMaxMhz) // leftover exact pin
                : null;

            if (lockMHz is not uint seen)
            {
                _clamp = null;
                return null;
            }

            // A matching readback keeps what this process knows about the
            // clamp — the lock it stands for, and the value that was WRITTEN
            // rather than the truncated echo of it (the B390 reads a 1500
            // request back as 1499.x; re-committing the echo drifted a lock
            // down a megahertz per refresh-then-restore cycle). 2 MHz, not 1:
            // the write paths accept a readback within 1.0 MHz of the unrounded
            // request, and both sides are rounded independently here. Anything
            // else is a new observation: whatever the old value's provenance
            // was does not transfer to a ceiling another process or a reboot
            // put there.
            _clamp = _clamp is { } tracked && Math.Abs((long)tracked.Mhz - seen) <= 2
                ? tracked
                : new TrackedClamp(seen, null);
            return _clamp.Value.Mhz;
        }
    }

    public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true, bool releaseLock = false)
    {
        lock (_applyLock)
        {
            var results = new List<KnobResult>();

            // Schema-validate with this device's own floors: Intel frequency
            // domains clamp down to 100 MHz, far below NVML's 210 MHz.
            uint lockFloor = Capabilities.LockClockMinMHz > 0 ? Capabilities.LockClockMinMHz : 100;
            double powerFloor = Capabilities is { SupportsPowerLimit: true, PowerLimitMinW: > 0 and < 50 }
                ? Capabilities.PowerLimitMinW
                : 50;
            if (profile.Validate(lockFloor, powerFloor) is string error)
            {
                results.Add(KnobResult.Fail("profile", error));
                return new ApplyResult(false, results);
            }

            if (profile.GpuUuid is { } target && GpuUuid is { } mine &&
                !string.Equals(target, mine, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(KnobResult.Fail("profile",
                    $"saved for a different GPU ({profile.GpuName ?? target}) — re-save it on this card to use it here"));
                return new ApplyResult(false, results);
            }

            if (releaseLock && profile.LockedCoreClockMHz is not null)
            {
                results.Add(KnobResult.Fail("profile",
                    $"the profile carries a {profile.LockedCoreClockMHz} MHz clock lock and the lock is to be released " +
                    "— two answers to one question; nothing was applied"));
                return new ApplyResult(false, results);
            }

            AppliedStateStore.RecordPending(profile.Name, RecordKey);

            ApplyPowerLimit(profile, results);
            RefuseIfRequested(profile.TempLimitC is not null, "temp limit", results);
            RefuseIfRequested(profile.VoltageBoostPct is not null, "voltage boost", results);
            RefuseIfRequested(profile.MemOffsetMHz != 0, "memory offset", results);
            RefuseIfRequested(profile.CoreOffsetMHz != 0, "core offset", results);
            ApplyLockedClock(profile.LockedCoreClockMHz, results, releaseLock);
            RefuseIfRequested(profile.VfPointOffsetsMHz is { Count: > 0 }, "V/F points", results);

            // `All` on an empty list is true, so an apply that touched nothing
            // would report success with an empty summary — the same shape as a
            // real one. ResetToDefaults already guards this; its twin did not.
            if (results.Count == 0)
            {
                results.Add(KnobResult.Ok(
                    "profile", "nothing in this profile applies to this Intel GPU; no writes were made"));
            }

            bool all = results.All(r => r.Applied);
            // Persist only a clamp Afterglow WROTE (this session, or inherited
            // from a crashed one). A ceiling ReadCurrent merely observed is not
            // an applied lock; persisting it — which happened whenever a release
            // or clamp write failed mid-apply — made the next launch inherit the
            // factory ceiling as "written by Afterglow", which the release path
            // then refused to adopt forever.
            uint? persistedLock = AppliedLockMHz; // written or inherited, never observed, never a probe pin
            AppliedStateStore.Record(profile, all, persistedLock, RecordKey);
            Log.Info($"Apply '{profile.Name}' (Arc): {(all ? "ok" : "PARTIAL")} — {string.Join("; ", results.Select(r => $"{r.Knob}={(r.Applied ? "ok" : "fail")}"))}");
            return new ApplyResult(all, results);
        }
    }

    public ApplyResult ResetToDefaults()
    {
        lock (_applyLock)
        {
            var results = new List<KnobResult>();

            if (Capabilities.SupportsLockedCoreClock)
            {
                results.Add(ReleaseClampCore());
            }

            if (Capabilities.SupportsPowerLimit && Capabilities.PowerLimitDefaultW > 0
                && EnsureOverclockWaiver("power limit", results))
            {
                double raw = _powerLimitInMilliwatts
                    ? Capabilities.PowerLimitDefaultW * 1000.0
                    : Capabilities.PowerLimitDefaultW;
                var rc = _device.TrySetOcPowerLimitV2(raw);
                if (rc != CtlResult.Success)
                {
                    results.Add(KnobResult.Fail("power limit", $"driver refused the default: {rc}"));
                }
                else
                {
                    // Same write-then-verify rule as the apply path and as the
                    // clamp release beside it: reporting the reset off the return
                    // code alone was the one remaining unverified success here.
                    var readRc = _device.TryGetOcPowerLimitV2(out double back);
                    double backW = _powerLimitInMilliwatts ? back / 1000.0 : back;
                    results.Add(readRc != CtlResult.Success
                        ? KnobResult.Fail("power limit",
                            $"driver accepted the default but the readback getter failed ({readRc}) — state unverified")
                        : Math.Abs(backW - Capabilities.PowerLimitDefaultW) > 1.5
                            ? KnobResult.Fail("power limit",
                                $"driver accepted the default but readback shows {backW:F0} W")
                            : KnobResult.Ok("power limit", $"{Capabilities.PowerLimitDefaultW:F0} W (verified)"));
                }
            }

            if (results.Count == 0)
            {
                results.Add(KnobResult.Ok("reset", "no tuning paths on this Intel GPU; nothing was applied"));
            }

            bool all = results.All(r => r.Applied);

            // Only a reset that fully landed may erase the applied-state record.
            // Clearing it after a PARTIAL reset threw away the one thing that
            // tells the next launch what is still on this card — the record
            // exists precisely for the case where the driver refused to undo it.
            if (all)
            {
                AppliedStateStore.Clear(RecordKey);
            }

            Log.Info($"Reset to defaults (Arc): {(all ? "ok" : "PARTIAL")}");
            return new ApplyResult(all, results);
        }
    }

    public KnobResult ForceUnlock()
    {
        lock (_applyLock)
        {
            if (!Capabilities.SupportsLockedCoreClock)
            {
                return KnobResult.Fail("clock lock", "the frequency clamp isn't available on this GPU");
            }

            return ReleaseClampCore();
        }
    }

    public NvmlReturn RestoreTuningLock(uint lockMHz) => WriteClampVerified(_hwMinMhz, lockMHz, lockMHz);

    public NvmlReturn LockClockForProbe(uint clockMHz) => WriteClampVerified(clockMHz, clockMHz, clockMHz);

    /// <inheritdoc />
    public ProbeStart BeginProbe()
    {
        lock (_applyLock)
        {
            if (!Capabilities.SupportsLockedCoreClock)
            {
                return new ProbeStart(0, null, null, "the frequency clamp isn't available on this GPU");
            }

            _probePinLanded = false;

            // Capture the displaced lock BEFORE the release empties the
            // tracking — the one ordering the probe could not get from outside.
            uint? observed = RefreshTrackedClampFromDriver();
            uint? displaced = _clamp?.AppliedLock;
            string? note = null;
            if (observed is uint clamp && clamp > 0)
            {
                // Release whatever clamp is on the card so the sweep runs
                // against the true ceiling: a leftover pin from a dead session
                // read as the ceiling and produced a two-point "complete" curve,
                // and a tuning lock hid the factory ceiling so the last target
                // was refused every time.
                var release = ReleaseClampCore();
                if (!release.Applied)
                {
                    return new ProbeStart(0, null, null, release.Detail);
                }

                if (displaced is null)
                {
                    note = $"the driver reported a {clamp} MHz clock ceiling this process had not applied " +
                        "(another tool's clamp, or the factory ceiling); it was released before the sweep, " +
                        "and the card is at its factory range";
                }
            }

            _probeDisplacedLock = displaced;
            return new ProbeStart(MaxLockableClockMHz, displaced, note);
        }
    }

    /// <inheritdoc />
    public KnobResult EndProbe()
    {
        lock (_applyLock)
        {
            if (_probeDisplacedLock is uint restore)
            {
                // As the RANGE lock profiles apply, never as an exact pin (which
                // would hold full clocks at idle). The verified write resolves
                // the probe's pin record; the displaced lock is forgotten only
                // once it is back on the card.
                var rc = RestoreTuningLock(restore);
                if (rc != NvmlReturn.Success)
                {
                    return KnobResult.Fail("clock lock", $"the previous {restore} MHz clock lock could not be restored ({rc})");
                }

                _probeDisplacedLock = null;
                return KnobResult.Ok("clock lock", $"restored the {restore} MHz lock");
            }

            if (!_probePinLanded)
            {
                // Nothing was ever pinned (step 0 refused, or the sweep never
                // started), so there is nothing to release — and a refused
                // release here used to be reported as "the GPU may still be
                // pinned" over a session that changed nothing.
                return KnobResult.Ok("clock lock", "nothing was pinned");
            }

            var release = ReleaseClampCore();
            if (!release.Applied)
            {
                return KnobResult.Fail("clock lock", $"the probe's clock lock could not be released ({release.Detail})");
            }

            _probePinLanded = false;
            return release;
        }
    }

    /// <summary>
    /// Writes a frequency range and CONFIRMS it by readback before reporting
    /// success. A pin is put on record BEFORE the write (a pin outlives a killed
    /// process, and the record is what the next launch reads); a refused write
    /// has pinned nothing, so a record THIS call created is resolved at once —
    /// one an earlier session left is still true and stays. When the driver
    /// accepted the write but the readback disagrees, the tracked value is what
    /// the card SETTLED at, not what was asked: a request above the factory
    /// ceiling settles at the ceiling (measured), and tracking the request
    /// left a shadow above anything the card ever showed.
    /// </summary>
    private NvmlReturn WriteClampVerified(double min, double max, uint shadow)
    {
        lock (_applyLock)
        {
            if (!Capabilities.SupportsLockedCoreClock)
            {
                return NvmlReturn.NotSupported;
            }

            bool isPin = Math.Abs(max - min) < 0.5;
            bool alreadyPending = isPin && AppliedStateStore.RecordProbeLockPending(RecordKey);

            var rc = _device.TrySetFrequencyRange(_gpuFreqDomain, min, max);
            if (rc != CtlResult.Success)
            {
                if (isPin && !alreadyPending)
                {
                    ResolveProbeRecord();
                }

                return ToNvml(rc);
            }

            // The write landed with the driver; the shadow records that a clamp
            // may now be present whether or not the readback confirms the value,
            // so a release is still attempted for it. A pin stands for no
            // applied lock — that is the displaced lock BeginProbe remembered.
            _clamp = new TrackedClamp(shadow, isPin ? null : shadow);
            if (isPin)
            {
                _probePinLanded = true;
            }

            if (_device.TryGetFrequencyRange(_gpuFreqDomain, out var readback) != CtlResult.Success)
            {
                // The capability was probed with this getter answering, so a
                // failure here is a real loss of confirmation, not a formality.
                return NvmlReturn.Unknown;
            }

            bool verified = Math.Abs(readback.Max - max) <= 1.0 && Math.Abs(readback.Min - min) <= 1.0;
            if (!verified && readback.Max > 0)
            {
                uint settled = (uint)Math.Round(readback.Max);
                _clamp = new TrackedClamp(settled, isPin ? null : settled);
            }

            if (verified && !isPin)
            {
                ResolveProbeRecord(); // a verified range lock this process wrote supersedes any pin
            }

            return verified ? NvmlReturn.Success : NvmlReturn.Unknown;
        }
    }

    public NvmlReturn SetAllFansRaw(uint dutyPct) => NvmlReturn.NotSupported;

    public NvmlReturn SetFanRaw(uint coolerId, uint dutyPct) => NvmlReturn.NotSupported;

    public NvmlReturn RestoreAutoFansRaw() => NvmlReturn.NotSupported;

    public NvapiStatus TryReadVfPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points)
    {
        points = [];
        return NvapiStatus.NotSupported;
    }

    public KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex) =>
        KnobResult.Fail("V/F points", "not supported on Intel GPUs");

    public KnobResult ClearVfPointOffsets() =>
        KnobResult.Fail("V/F points", "not supported on Intel GPUs");

    private void ApplyLockedClock(uint? target, List<KnobResult> results, bool releaseLock)
    {
        if (target is uint lockMHz)
        {
            if (!Capabilities.SupportsLockedCoreClock)
            {
                results.Add(KnobResult.Fail("locked core clock", "the frequency clamp isn't available on this GPU"));
                return;
            }

            double clamped = Math.Clamp(lockMHz, _hwMinMhz, _hwMaxMhz);

            // Clamping to the hardware maximum IS the unrestricted range: the
            // driver would hold no limit at all, and ReadCurrent would rightly
            // report "none" a second later. Reporting that as a verified lock
            // told the user the core was pinned when nothing had been applied,
            // and made `set` and `get` contradict each other. Refuse instead.
            if (clamped >= ReleasedMaxMhz - 0.5)
            {
                // Quote the value actually being tested. ReleasedMaxMhz is the
                // observed unclamped ceiling and can sit below the domain max
                // after a release refines it; quoting the domain max instead
                // produced "X is at or above this GPU's maximum (Y)" with X < Y.
                results.Add(KnobResult.Fail(
                    "locked core clock",
                    $"{lockMHz} MHz is at or above this GPU's unclamped ceiling ({ReleasedMaxMhz:F0} MHz), " +
                    "so the clamp would be the unrestricted range — nothing was applied. " +
                    "Pick a lower clock to pin it."));
                return;
            }

            string clampNote = Math.Abs(clamped - lockMHz) > 0.5
                ? $" (requested {lockMHz}, clamped to hardware range {_hwMinMhz:F0}..{_hwMaxMhz:F0})"
                : string.Empty;

            var rc = _device.TrySetFrequencyRange(_gpuFreqDomain, _hwMinMhz, clamped);
            if (rc != CtlResult.Success)
            {
                results.Add(KnobResult.Fail("locked core clock", $"driver refused the clamp: {rc}"));
                return;
            }

            // Set at the WRITE, not after verification: the driver may be holding
            // a clamp even when the readback getter fails or disagrees, and that
            // is exactly when the implicit release matters most. Setting it only
            // on the verified path left those clamps on the card, with a
            // lock-less apply reporting full success over a still-pinned GPU.
            _clamp = new TrackedClamp((uint)Math.Round(clamped), (uint)Math.Round(clamped));
            string detail = $"{_hwMinMhz:F0}..{clamped:F0} MHz{clampNote}";
            var readRc = _device.TryGetFrequencyRange(_gpuFreqDomain, out var readback);
            if (readRc != CtlResult.Success)
            {
                // The capability was probed with this getter answering, so a
                // failure here is loud, not a silent unverified success.
                results.Add(KnobResult.Fail("locked core clock",
                    $"driver accepted the clamp but the readback getter failed ({readRc}) — state unverified"));
                return;
            }

            if (Math.Abs(readback.Max - clamped) > 1.0)
            {
                // Track what the card SETTLED at, not what was asked: a request
                // above the factory ceiling settles at the ceiling (measured),
                // and tracking the request left a shadow above anything the
                // card ever showed — un-releasable in a fresh process.
                if (readback.Max > 0)
                {
                    uint settled = (uint)Math.Round(readback.Max);
                    _clamp = new TrackedClamp(settled, settled);
                }

                results.Add(KnobResult.Fail("locked core clock",
                    $"driver accepted the call but readback shows {readback.Min:F0}..{readback.Max:F0} MHz"));
                return;
            }

            ResolveProbeRecord(); // a verified range lock this process wrote supersedes any probe pin
            results.Add(KnobResult.Ok("locked core clock", detail + " (verified)"));
            return;
        }

        // Profile carries no lock: release any tracked clamp — visibly, never
        // silently — whether this session wrote it or inherited it from a
        // crashed session's record. Releasing only clamps written this session
        // left an inherited one on the card behind a FAIL knob, and the App has
        // no explicit-unlock path of its own, so a lock-less profile applied
        // from automation, startup or the Profiles tab could never undo a clamp
        // the App itself had persisted before a crash. A ceiling ReadCurrent
        // committed that turns out to be the factory value is handled inside
        // ReleaseClampCore, which adopts it as the released baseline rather
        // than failing every apply.
        if (!Capabilities.SupportsLockedCoreClock)
        {
            if (releaseLock)
            {
                // The explicit request still gets its one verdict — the
                // answer ForceUnlock gives — rather than an empty result that
                // the front-ends print as success.
                results.Add(KnobResult.Fail("clock lock", "the frequency clamp isn't available on this GPU"));
            }

            return;
        }

        // One live read decides, through the same primitive ReadCurrent uses.
        // It commits a clamp the driver shows — so a fresh CLI process (certify,
        // set, MCP), which has never read the range, sees exactly what the
        // App's Tuning page would have and can release it before the burn
        // rather than have ProfileWasReset refuse the certification afterwards
        // — and it clears a shadow the driver no longer confirms (a clamp
        // released by another process, or by a reboot since the applied state
        // was persisted). The shadow alone stated hardware facts nobody had read.
        if (RefreshTrackedClampFromDriver() is not uint previous)
        {
            if (releaseLock)
            {
                // An explicit release with nothing tracked still goes to the
                // driver, through the same verified release, so the request
                // yields one verdict rather than a front-end's guess.
                results.Add(ReleaseClampCore());
            }

            return;
        }

        var release = ReleaseClampCore();
        results.Add(release with { Detail = $"was {_hwMinMhz:F0}..{previous} MHz — {release.Detail}" });
    }

    /// <summary>
    /// Releases the clamp (-1/-1 = factory values) and judges the result from
    /// the readback. The driver's factory restore returns the factory range
    /// (measured on the B390 from a range lock, a pin, a narrow range and when
    /// already at factory), so after the floor gate the only way a release can
    /// be wrong is a ceiling BELOW one this process has verified released
    /// before — that, and only that, fails. A ceiling that merely stayed where
    /// the clamp was, with nothing higher ever verified, is adopted as the
    /// GPU's factory ceiling and said so: the header allows a factory ceiling
    /// below the domain maximum, and refusing it made the phantom permanent
    /// (every lock-less apply PARTIAL, the record never clearable). This rule
    /// cannot tell a cap the driver kept from a factory ceiling in a process
    /// that has never seen higher — no rule can; no driver has been measured
    /// keeping a cap, and the fake device does not model one.
    /// </summary>
    private KnobResult ReleaseClampCore()
    {
        TrackedClamp? previous = _clamp;
        var rc = _device.TrySetFrequencyRange(_gpuFreqDomain, -1, -1);
        if (rc != CtlResult.Success)
        {
            return KnobResult.Fail("clock lock", $"driver refused the release: {rc}");
        }

        var readRc = _device.TryGetFrequencyRange(_gpuFreqDomain, out var readback);
        if (readRc != CtlResult.Success)
        {
            return KnobResult.Fail("clock lock",
                $"release accepted but the readback getter failed ({readRc}) — clamp state unverified");
        }

        string stillShows =
            $"release accepted but readback still shows {readback.Min:F0}..{readback.Max:F0} MHz";

        // An exact pin raises the floor; a release that leaves it raised is not
        // a release.
        if (readback.Min > 0 && readback.Min > _hwMinMhz + 1.0)
        {
            return KnobResult.Fail("clock lock", stillShows);
        }

        // The driver reports no limit at all — unambiguously released.
        if (readback.Max <= 0)
        {
            AdoptReleasedBaseline(_hwMaxMhz);
            return KnobResult.Ok("clock lock", "released (driver reports no frequency limit) (verified)");
        }

        const double Epsilon = 0.5;
        if (_observedReleasedMaxMhz is double known && readback.Max + Epsilon < known)
        {
            return KnobResult.Fail("clock lock", stillShows);
        }

        bool proven = readback.Max >= _hwMaxMhz - Epsilon
            || (_observedReleasedMaxMhz is double verifiedCeiling && readback.Max + Epsilon >= verifiedCeiling)
            || (previous is { } tracked && readback.Max > tracked.Mhz + Epsilon);
        AdoptReleasedBaseline(readback.Max);
        return proven
            ? KnobResult.Ok("clock lock", $"released to {readback.Min:F0}..{readback.Max:F0} MHz (verified)")
            : KnobResult.Ok("clock lock",
                $"released; the factory restore left the ceiling at {readback.Min:F0}..{readback.Max:F0} MHz, " +
                "taken as this GPU's factory ceiling (nothing higher has been verified in this process)");
    }

    /// <summary>
    /// IGCL refuses overclock-block writes until the session has signed the
    /// driver's overclocking waiver (<c>CTL_RESULT_ERROR_CORE_OVERCLOCK_WAIVER_NOT_SET</c>).
    /// Nothing signed it, so every power-limit write and every power-limit reset
    /// would have been refused on the discrete hardware the knob lights up on.
    /// It is signed lazily, immediately before the first such write — always an
    /// explicit, elevation-gated change the user asked for — and never during
    /// probing, so a read-only session signs nothing. The frequency clamp is a
    /// frequency-domain call, needs no waiver, and is unaffected.
    /// </summary>
    private bool EnsureOverclockWaiver(string knob, List<KnobResult> results)
    {
        if (_waiverSigned)
        {
            return true;
        }

        var rc = _device.TrySetOverclockWaiver();
        if (rc != CtlResult.Success)
        {
            results.Add(KnobResult.Fail(knob,
                $"the driver refused the overclocking waiver ({rc}) — no overclock write can be made"));
            return false;
        }

        _waiverSigned = true;
        return true;
    }

    private void ApplyPowerLimit(TuningProfile profile, List<KnobResult> results)
    {
        if (profile.PowerLimitW is not double watts)
        {
            return;
        }

        if (!Capabilities.SupportsPowerLimit)
        {
            results.Add(KnobResult.Fail("power limit", "not exposed by this device's driver"));
            return;
        }

        var (clamped, wasClamped) = TuningMath.ClampPower(watts, Capabilities.PowerLimitMinW, Capabilities.PowerLimitMaxW);
        string detail = wasClamped
            ? $"{clamped:F0} W (requested {watts:F0}, clamped to {Capabilities.PowerLimitMinW:F0}..{Capabilities.PowerLimitMaxW:F0})"
            : $"{clamped:F0} W";

        if (!EnsureOverclockWaiver("power limit", results))
        {
            return;
        }

        double raw = _powerLimitInMilliwatts ? clamped * 1000.0 : clamped;
        var rc = _device.TrySetOcPowerLimitV2(raw);
        if (rc != CtlResult.Success)
        {
            results.Add(KnobResult.Fail("power limit", $"driver refused: {rc}"));
            return;
        }

        // Same rule as the clamp: a write the getter cannot confirm is a
        // failure, never a silent unverified success. Falling through to Ok
        // here reported an unread write as applied.
        var readRc = _device.TryGetOcPowerLimitV2(out double readbackRaw);
        if (readRc != CtlResult.Success)
        {
            results.Add(KnobResult.Fail("power limit",
                $"driver accepted the write but the readback getter failed ({readRc}) — state unverified"));
            return;
        }

        double readbackW = _powerLimitInMilliwatts ? readbackRaw / 1000.0 : readbackRaw;
        if (Math.Abs(readbackW - clamped) > 1.5)
        {
            results.Add(KnobResult.Fail("power limit",
                $"driver accepted the call but readback shows {readbackW:F0} W"));
            return;
        }

        results.Add(KnobResult.Ok("power limit", detail + " (verified)"));
    }

    private static void RefuseIfRequested(bool requested, string knob, List<KnobResult> results)
    {
        if (requested)
        {
            results.Add(KnobResult.Fail(knob, "not supported on this Intel GPU"));
        }
    }

    private static NvmlReturn ToNvml(CtlResult rc) => rc switch
    {
        CtlResult.Success => NvmlReturn.Success,
        CtlResult.ErrorUnsupportedFeature or CtlResult.FunctionNotFound or CtlResult.LibraryNotFound
            or CtlResult.ErrorNotAvailable => NvmlReturn.NotSupported,
        CtlResult.ErrorInsufficientPermissions => NvmlReturn.NoPermission,
        CtlResult.ErrorInvalidArgument => NvmlReturn.InvalidArgument,
        _ => NvmlReturn.Unknown,
    };
}
