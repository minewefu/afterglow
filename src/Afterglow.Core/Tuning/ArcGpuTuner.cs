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
    private readonly IgclDevice _device;
    private readonly nint _gpuFreqDomain;
    private readonly double _hwMinMhz;
    private readonly double _hwMaxMhz;
    private readonly bool _powerLimitInMilliwatts;
    private readonly object _applyLock = new();

    private uint? _appliedLockMHz;

    /// <summary>
    /// The max the driver reads back when no clamp is active. Usually the
    /// hardware max, but the header says the -1 "factory value" restore can
    /// land lower — observed released values keep this honest so a healthy
    /// released card is never reported as clamped.
    /// </summary>
    private double _releasedMaxMhz;

    public ArcGpuTuner(IgclDevice device, string? gpuUuid)
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
                && IgclDevice.TryGetFrequencyRange(handle, out _) == CtlResult.Success;
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
            _appliedLockMHz = tracked;
        }

        // Released-max baseline: the hardware max, refined by observation —
        // but never from a range that our own tracked clamp is still shaping.
        _releasedMaxMhz = _hwMaxMhz;
        if (supportsClamp && _appliedLockMHz is null
            && IgclDevice.TryGetFrequencyRange(_gpuFreqDomain, out var initial) == CtlResult.Success
            && initial.Max > 0 && initial.Max < _hwMaxMhz)
        {
            _releasedMaxMhz = initial.Max;
        }
    }

    public TuningCapabilities Capabilities { get; }

    public string? GpuUuid { get; }

    // Locked like GpuTuner's: Nullable<uint> reads are not atomic, and probe
    // restore paths capture this value from other threads.
    public uint? AppliedLockMHz
    {
        get
        {
            lock (_applyLock)
            {
                return _appliedLockMHz;
            }
        }
    }

    /// <summary>The IGCL device, for later milestones' write paths.</summary>
    public IgclDevice Device => _device;

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
        uint? lockMHz;
        if (_gpuFreqDomain != 0
            && IgclDevice.TryGetFrequencyRange(_gpuFreqDomain, out var range) == CtlResult.Success)
        {
            bool maxClamped = range.Max > 0 && range.Max < _releasedMaxMhz - 0.5;
            bool minRaised = range.Min > 0 && range.Min > _hwMinMhz + 0.5;
            lockMHz = maxClamped ? (uint)Math.Round(range.Max)
                : minRaised ? (uint)Math.Round(range.Max > 0 ? range.Max : _hwMaxMhz) // leftover exact pin
                : null;

            lock (_applyLock)
            {
                _appliedLockMHz = lockMHz;
            }
        }
        else
        {
            lockMHz = AppliedLockMHz;
        }

        return (0, 0, powerW, null, lockMHz);
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

            bool all = results.All(r => r.Applied);
            AppliedStateStore.Record(profile, all, _appliedLockMHz, GpuUuid);
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

            if (Capabilities.SupportsPowerLimit && Capabilities.PowerLimitDefaultW > 0)
            {
                double raw = _powerLimitInMilliwatts
                    ? Capabilities.PowerLimitDefaultW * 1000.0
                    : Capabilities.PowerLimitDefaultW;
                var rc = _device.TrySetOcPowerLimitV2(raw);
                results.Add(rc == CtlResult.Success
                    ? KnobResult.Ok("power limit", $"{Capabilities.PowerLimitDefaultW:F0} W")
                    : KnobResult.Fail("power limit", $"driver refused the default: {rc}"));
            }

            if (results.Count == 0)
            {
                results.Add(KnobResult.Ok("reset", "no tuning paths on this Intel GPU; nothing was applied"));
            }

            bool all = results.All(r => r.Applied);
            AppliedStateStore.Clear(GpuUuid);
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

    public NvmlReturn RestoreTuningLock(uint lockMHz)
    {
        lock (_applyLock)
        {
            if (!Capabilities.SupportsLockedCoreClock)
            {
                return NvmlReturn.NotSupported;
            }

            var rc = IgclDevice.TrySetFrequencyRange(_gpuFreqDomain, _hwMinMhz, lockMHz);
            if (rc == CtlResult.Success)
            {
                _appliedLockMHz = lockMHz;
            }

            return ToNvml(rc);
        }
    }

    public NvmlReturn LockClockForProbe(uint clockMHz)
    {
        lock (_applyLock)
        {
            if (!Capabilities.SupportsLockedCoreClock)
            {
                return NvmlReturn.NotSupported;
            }

            return ToNvml(IgclDevice.TrySetFrequencyRange(_gpuFreqDomain, clockMHz, clockMHz));
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
            string clampNote = Math.Abs(clamped - lockMHz) > 0.5
                ? $" (requested {lockMHz}, clamped to hardware range {_hwMinMhz:F0}..{_hwMaxMhz:F0})"
                : string.Empty;

            var rc = IgclDevice.TrySetFrequencyRange(_gpuFreqDomain, _hwMinMhz, clamped);
            if (rc != CtlResult.Success)
            {
                results.Add(KnobResult.Fail("locked core clock", $"driver refused the clamp: {rc}"));
                return;
            }

            _appliedLockMHz = (uint)Math.Round(clamped);
            string detail = $"{_hwMinMhz:F0}..{clamped:F0} MHz{clampNote}";
            var readRc = IgclDevice.TryGetFrequencyRange(_gpuFreqDomain, out var readback);
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

            results.Add(KnobResult.Ok("locked core clock", detail + " (verified)"));
            return;
        }

        // Profile carries no lock: release any active one — visibly, never silently.
        if (_appliedLockMHz is uint previous && Capabilities.SupportsLockedCoreClock)
        {
            var release = ReleaseClampCore();
            results.Add(release with { Detail = $"removed (was {_hwMinMhz:F0}..{previous} MHz) — {release.Detail}" });
        }
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
        uint? previous = _appliedLockMHz;
        var rc = IgclDevice.TrySetFrequencyRange(_gpuFreqDomain, -1, -1);
        if (rc != CtlResult.Success)
        {
            return KnobResult.Fail("clock lock", $"driver refused the release: {rc}");
        }

        var readRc = IgclDevice.TryGetFrequencyRange(_gpuFreqDomain, out var readback);
        if (readRc != CtlResult.Success)
        {
            return KnobResult.Fail("clock lock",
                $"release accepted but the readback getter failed ({readRc}) — clamp state unverified");
        }

        bool released = readback.Max <= 0 || previous is not uint prev || readback.Max > prev + 1.0;
        if (!released)
        {
            return KnobResult.Fail("clock lock",
                $"release accepted but readback still shows {readback.Min:F0}..{readback.Max:F0} MHz");
        }

        _appliedLockMHz = null;
        if (readback.Max > 0)
        {
            _releasedMaxMhz = readback.Max;
            return KnobResult.Ok("clock lock", $"released to {readback.Min:F0}..{readback.Max:F0} MHz (verified)");
        }

        return KnobResult.Ok("clock lock", "released (driver reports no frequency limit) (verified)");
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

        double raw = _powerLimitInMilliwatts ? clamped * 1000.0 : clamped;
        var rc = _device.TrySetOcPowerLimitV2(raw);
        if (rc != CtlResult.Success)
        {
            results.Add(KnobResult.Fail("power limit", $"driver refused: {rc}"));
            return;
        }

        if (_device.TryGetOcPowerLimitV2(out double readbackRaw) == CtlResult.Success)
        {
            double readbackW = _powerLimitInMilliwatts ? readbackRaw / 1000.0 : readbackRaw;
            if (Math.Abs(readbackW - clamped) > 1.5)
            {
                results.Add(KnobResult.Fail("power limit",
                    $"driver accepted the call but readback shows {readbackW:F0} W"));
                return;
            }

            detail += " (verified)";
        }

        results.Add(KnobResult.Ok("power limit", detail));
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
