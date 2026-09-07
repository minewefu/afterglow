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

    /// <summary>Who put the tracked clamp on the card.</summary>
    private enum ClampProvenance
    {
        /// <summary>
        /// ReadCurrent saw it; nothing in this process or a recorded session
        /// wrote it. It may be the GPU's factory ceiling (which no release can
        /// raise), another tool's clamp, or a pin a dead session left behind.
        /// The release path may adopt an observed RANGE ceiling that a factory
        /// restore leaves in place as the factory value; never a written one.
        /// </summary>
        Observed,

        /// <summary>
        /// Adopted from a crashed session's applied-state record. Afterglow
        /// wrote it, so it is never a factory value: a release that leaves it
        /// in place is a FAILED release.
        /// </summary>
        Inherited,

        /// <summary>This process wrote it — a range lock, or the probe's exact pin.</summary>
        Written,
    }

    /// <summary>The clamp's shape, as the driver reads it back.</summary>
    private enum ClampShape
    {
        /// <summary>Floor at the domain minimum, ceiling at the clamp (the tuning lock).</summary>
        Range,

        /// <summary>
        /// Floor and ceiling at the clamp (the probe's pin). Its release is
        /// proven by the FLOOR dropping back to the minimum with the ceiling
        /// back at the highest one this process has seen; the rising-ceiling
        /// test that proves a range lock's release cannot prove a pin's on a
        /// card whose factory ceiling sits below the domain maximum, where the
        /// probe's last pin IS that ceiling.
        /// </summary>
        ExactPin,
    }

    /// <summary>
    /// The clamp this tuner believes is on the card. One value, so provenance
    /// and shape cannot exist without a clamp, and a clamp cannot be both
    /// written and inherited — the four booleans this replaced had to be
    /// reset together by hand at every write site.
    /// </summary>
    private readonly record struct TrackedClamp(uint Mhz, ClampProvenance Provenance, ClampShape Shape)
    {
        public bool AfterglowWrote => Provenance != ClampProvenance.Observed;

        public bool IsPin => Shape == ClampShape.ExactPin;
    }

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

    /// <inheritdoc />
    public string? ProbeRecordKey { get; set; }

    /// <summary>
    /// The highest ceiling any readback has shown this process: the released
    /// baseline once observed, or the ceiling ReadCurrent saw before a probe.
    /// An exact pin's release is proven by the floor dropping — but only with
    /// the ceiling back at this. A ceiling stuck AT the pin (the driver dropped
    /// the floor and kept the cap) read as "released" and was adopted as the
    /// new baseline, hiding a still-capped card for the rest of the session.
    /// </summary>
    private double _ceilingHighWaterMhz;

    /// <inheritdoc />
    public uint MaxLockableClockMHz =>
        _observedReleasedMaxMhz is double known && known > 0 ? (uint)Math.Round(known) : Capabilities.MaxCoreClockMHz;

    /// <summary>
    /// A verified release: the readback ceiling becomes the released baseline,
    /// the tracked clamp is cleared, and any V/F probe record for this card is
    /// resolved — the release just proved its pin is gone. One helper: the
    /// triplet was pasted at four sites and a fifth had to remember it.
    /// </summary>
    private void AdoptReleasedBaseline(double max, bool verified = true)
    {
        _clamp = null;
        _observedReleasedMaxMhz = max;
        _ceilingHighWaterMhz = Math.Max(_ceilingHighWaterMhz, max);
        if (verified)
        {
            // Only a VERIFIED release proves a probe's pin gone; the
            // "rise not verifiable" adoption of a factory ceiling does not.
            ResolveProbeRecord();
        }
    }

    /// <summary>See <see cref="IGpuTuner.ProbeRecordKey"/>.</summary>
    private void ResolveProbeRecord()
    {
        if ((ProbeRecordKey ?? GpuUuid) is { } key)
        {
            AppliedStateStore.ResolveProbeLock(key);
        }
    }

    public ArcGpuTuner(IArcDevice device, string? gpuUuid)
    {
        _device = device;
        GpuUuid = gpuUuid;

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

        // Adopt a tracked clamp only from a record stamped for this GPU —
        // same identity guard as the NVIDIA tuner (legacy unstamped records
        // never belong to an Intel identity; see AppliedStateStore.Load).
        if (AppliedStateStore.Load(gpuUuid) is { LockedCoreClockMHz: uint tracked })
        {
            _clamp = new TrackedClamp(tracked, ClampProvenance.Inherited, ClampShape.Range);
        }
    }

    public TuningCapabilities Capabilities { get; }

    public string? GpuUuid { get; }

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
                return _clamp is { AfterglowWrote: true, IsPin: false } applied ? applied.Mhz : null;
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

            _ceilingHighWaterMhz = Math.Max(_ceilingHighWaterMhz, range.Max);
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

            // Provenance transfers only to the same value. 2 MHz, not 1: the
            // write paths accept a readback within 1.0 MHz of the UNROUNDED
            // request (the B390 driver truncates 1499.6 to 1499), and both
            // sides are rounded independently here, so a driver that settles
            // a fraction off could pass verification and then have its own
            // verified write reclassified as merely observed on the next read.
            var provenance = _clamp is { } tracked && Math.Abs((long)tracked.Mhz - seen) <= 2
                ? tracked.Provenance
                : ClampProvenance.Observed;

            // The shape is the driver's, whatever the history: a pin is a
            // raised floor. Another process may have replaced this session's
            // pin with a range lock at the same ceiling, and a pin left by a
            // dead session is a pin from its very first read — the release
            // path refuses to adopt one as a factory ceiling.
            _clamp = new TrackedClamp(seen, provenance, minRaised ? ClampShape.ExactPin : ClampShape.Range);
            return seen;
        }
    }

    public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true)
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

            AppliedStateStore.RecordPending(profile.Name, GpuUuid);

            ApplyPowerLimit(profile, results);
            RefuseIfRequested(profile.TempLimitC is not null, "temp limit", results);
            RefuseIfRequested(profile.VoltageBoostPct is not null, "voltage boost", results);
            RefuseIfRequested(profile.MemOffsetMHz != 0, "memory offset", results);
            RefuseIfRequested(profile.CoreOffsetMHz != 0, "core offset", results);
            ApplyLockedClock(profile.LockedCoreClockMHz, results);
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
            AppliedStateStore.Record(profile, all, persistedLock, GpuUuid);
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
                AppliedStateStore.Clear(GpuUuid);
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

    /// <summary>
    /// Writes a frequency range and CONFIRMS it by readback before reporting
    /// success or recording the shadow.
    /// <para>
    /// These two were the only clamp writes in this class that reported the
    /// driver's return code alone, contradicting the class header's promise that
    /// "clamp applies and releases verify or fail loudly". Their callers treat
    /// Success as proof: the V/F probe prints "previous clock state restored",
    /// <c>CancelProbeAndWait</c> returns true and shutdown stamps the session
    /// clean, and the CLI emits <c>probe_clock_restored: true</c> — all off a
    /// call the driver may have accepted and ignored, leaving the card pinned.
    /// </para>
    /// </summary>
    private NvmlReturn WriteClampVerified(double min, double max, uint shadow)
    {
        lock (_applyLock)
        {
            if (!Capabilities.SupportsLockedCoreClock)
            {
                return NvmlReturn.NotSupported;
            }

            var rc = _device.TrySetFrequencyRange(_gpuFreqDomain, min, max);
            if (rc != CtlResult.Success)
            {
                return ToNvml(rc);
            }

            // The write landed with the driver; the shadow records that a clamp
            // may now be present whether or not the readback confirms the value,
            // so a release is still attempted for it.
            _clamp = new TrackedClamp(
                shadow,
                ClampProvenance.Written, // this process's write supersedes an inherited record
                Math.Abs(max - min) < 0.5 ? ClampShape.ExactPin : ClampShape.Range);

            if (_device.TryGetFrequencyRange(_gpuFreqDomain, out var readback) != CtlResult.Success)
            {
                // The capability was probed with this getter answering, so a
                // failure here is a real loss of confirmation, not a formality.
                return NvmlReturn.Unknown;
            }

            return Math.Abs(readback.Max - max) <= 1.0 && Math.Abs(readback.Min - min) <= 1.0
                ? NvmlReturn.Success
                : NvmlReturn.Unknown;
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

    private void ApplyLockedClock(uint? target, List<KnobResult> results)
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
            _clamp = new TrackedClamp((uint)Math.Round(clamped), ClampProvenance.Written, ClampShape.Range);
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
            return;
        }

        var release = ReleaseClampCore();
        results.Add(release with { Detail = $"was {_hwMinMhz:F0}..{previous} MHz — {release.Detail}" });
    }

    /// <summary>
    /// Releases the clamp (-1/-1 = factory values) and verifies via readback.
    /// The header warns the factory max can sit below the hardware max, so
    /// "released" means "no longer at the clamp we applied", not "back to the
    /// hardware max" — and the observed factory value refines the released
    /// baseline ReadCurrent compares against. The tracked shadow is cleared
    /// only after the release verifies, so a failed release keeps state
    /// truthful and the next apply retries it.
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

        // Nothing below may weaken beta.1's rule, which was correct: if we were
        // tracking a clamp, the ceiling must have risen above it, full stop.
        // Every state mutation happens only after a check passes, so a failed
        // release leaves the shadow and the baseline exactly as they were and
        // the next apply retries it.
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

        // A clamp we were tracking is decisive, and it is checked FIRST. The
        // domain-max shortcut below reads "nothing can be clamped at the
        // maximum", but it compares with a 1 MHz tolerance while the apply gate
        // refuses only at or above the released ceiling — so exactly one value,
        // hwMax-1, can be clamped, survive a release, and still satisfy the
        // shortcut. Ordering the tracked rule above it closes that window
        // without weakening either test.
        if (previous is { } tracked)
        {
            uint prev = tracked.Mhz;
            // A release must show a ceiling ABOVE the tracked clamp. The single
            // exception is a clamp tracked AT the frequency domain's own maximum,
            // which nothing can exceed — there the rise test is unsatisfiable, and
            // a ceiling back at the domain max is the released state by
            // definition (the floor gate above has already proven no exact pin
            // survives).
            //
            // The tolerance matters. At 1.0 MHz the exception swallowed
            // hwMax-1 as well, so a clamp at 2299 that the driver did NOT release
            // read back 2299 and was reported "released to 100..2299 MHz
            // (verified)" — a false verified release, which is worse than the
            // false FAILURE this exception was added to fix. At 0.5 MHz a real
            // release from 2299 reads 2300 and satisfies the rise test on its
            // own, so the exception is needed only where it is actually true.
            const double Epsilon = 0.5;
            bool ceilingRose = readback.Max > prev + Epsilon;
            bool clampWasAtDomainMax = prev + Epsilon >= _hwMaxMhz;
            bool ceilingAtDomainMax = readback.Max >= _hwMaxMhz - Epsilon;
            // An exact pin is proven released by the floor gate above — its
            // floor sat AT the pinned frequency and is now back at the minimum
            // — provided the ceiling did not drop below the pin. The ceiling
            // need not rise: the probe's last pin on a card whose factory
            // ceiling is below the domain maximum sits at that ceiling.
            // ...and the ceiling is back at the highest one this process has
            // seen: a ceiling stuck AT the pin is a cap the driver kept.
            // Only a pin THIS process wrote: it knows the ceiling it pinned
            // under. A pin merely observed (left by a dead session) has an
            // unknown ceiling, so its release is proven only by a rise.
            bool exactPinReleased = tracked is { IsPin: true, Provenance: ClampProvenance.Written }
                && readback.Max + Epsilon >= Math.Max(prev, _ceilingHighWaterMhz);
            if (!ceilingRose && !(clampWasAtDomainMax && ceilingAtDomainMax) && !exactPinReleased)
            {
                // A clamp Afterglow WROTE — in this process, or in a crashed
                // session whose record was inherited — is never a factory
                // value, so a release that leaves it in place has failed, full
                // stop. Adopting an inherited clamp here would have hidden a
                // real pinned card behind "factory ceiling" and cleared the
                // only record of it.
                if (tracked.AfterglowWrote)
                {
                    return KnobResult.Fail("clock lock", stillShows);
                }

                // An observed PIN is never a factory ceiling either: its floor
                // was raised, and a ceiling that stays at the pin after the
                // floor dropped is a cap the driver kept. Adopting it hid a
                // still-capped card for the rest of the session.
                if (tracked.IsPin)
                {
                    return KnobResult.Fail("clock lock", stillShows);
                }

                // The tracked value was only ever OBSERVED: ReadCurrent committed
                // it because the ceiling sits below the domain maximum. The
                // driver has just accepted a factory restore and the floor is
                // back at minimum, yet the ceiling did not move — that is the
                // factory ceiling, not a clamp. Failing here made the phantom
                // permanent: every lock-less apply came back PARTIAL and
                // persisted the ceiling as a lock, and no reset could ever clear
                // it. Adopt it as the released baseline so ReadCurrent stops
                // reporting it.
                Log.Warn(
                    $"Clock-lock release (Arc): the ceiling stayed at {readback.Max:F0} MHz after a factory restore " +
                    $"and the tracked {prev} MHz was only observed, never written — treating it as the factory ceiling.");
                AdoptReleasedBaseline(readback.Max, verified: false);
                return KnobResult.Ok("clock lock",
                    $"released; the factory restore left the ceiling at {readback.Min:F0}..{readback.Max:F0} MHz, " +
                    "so that is this GPU's factory ceiling, not a clamp (rise not verifiable)");
            }

            AdoptReleasedBaseline(readback.Max);
            return KnobResult.Ok("clock lock", $"released to {readback.Min:F0}..{readback.Max:F0} MHz (verified)");
        }

        // Nothing tracked, and the ceiling is back at the frequency domain's own
        // maximum: proof on its own, needing no baseline, and the ordinary case
        // on hardware whose factory range spans the whole domain.
        if (readback.Max >= _hwMaxMhz - 1.0)
        {
            AdoptReleasedBaseline(readback.Max);
            return KnobResult.Ok("clock lock", $"released to {readback.Min:F0}..{readback.Max:F0} MHz (verified)");
        }

        // No tracked clamp, but we have seen this GPU's unclamped ceiling before:
        // the readback has to reach it.
        if (_observedReleasedMaxMhz is double knownCeiling)
        {
            if (readback.Max < knownCeiling - 1.0)
            {
                return KnobResult.Fail("clock lock", stillShows);
            }

            _clamp = null;
            ResolveProbeRecord(); // verified against the known ceiling
            return KnobResult.Ok("clock lock", $"released to {readback.Min:F0}..{readback.Max:F0} MHz (verified)");
        }

        // Neither: this is a ForceUnlock in a process that has never seen this
        // GPU unclamped, so a ceiling below the domain maximum is genuinely
        // ambiguous — it is either the factory ceiling or a clamp that outlived
        // a crashed session, and no reading here can tell them apart. Report the
        // release honestly WITHOUT the word "verified", and do NOT adopt the
        // reading as the baseline: adopting it would hide a surviving clamp from
        // every later read, and hiding a clamp is far worse than surfacing a
        // limit that turns out to be the factory one.
        _clamp = null;
        return KnobResult.Ok(
            "clock lock",
            $"released to {readback.Min:F0}..{readback.Max:F0} MHz (unverified — nothing was clamped by this " +
            $"session, so a {readback.Max:F0} MHz ceiling cannot be told apart from this GPU's factory limit)");
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
