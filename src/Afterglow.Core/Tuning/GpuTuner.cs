using System.Text.Json;
using Afterglow.Core.Diagnostics;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;

namespace Afterglow.Core.Tuning;

/// <summary>What the driver reports as tunable on this GPU, with legal ranges.</summary>
public sealed record TuningCapabilities
{
    public bool SupportsCoreOffset { get; init; }
    public int CoreOffsetMinMHz { get; init; }
    public int CoreOffsetMaxMHz { get; init; }

    public bool SupportsMemOffset { get; init; }
    public int MemOffsetMinMHz { get; init; }
    public int MemOffsetMaxMHz { get; init; }

    public bool SupportsPowerLimit { get; init; }
    public double PowerLimitMinW { get; init; }
    public double PowerLimitMaxW { get; init; }
    public double PowerLimitDefaultW { get; init; }

    public bool SupportsLockedCoreClock { get; init; }
    public uint MaxCoreClockMHz { get; init; }

    public bool SupportsFanControl { get; init; }
    public uint FanCount { get; init; }
    public uint FanMinDutyPct { get; init; }

    public bool SupportsVoltageBoost { get; init; }

    public bool SupportsTempLimit { get; init; }
    public int TempLimitMinC { get; init; }
    public int TempLimitMaxC { get; init; }
    public int TempLimitDefaultC { get; init; }
}

/// <summary>Pure clamp/validation helpers (unit-tested separately from hardware).</summary>
public static class TuningMath
{
    public static (int Value, bool WasClamped) ClampOffset(int requested, int min, int max)
    {
        int clamped = Math.Clamp(requested, min, max);
        return (clamped, clamped != requested);
    }

    public static (double Value, bool WasClamped) ClampPower(double requestedW, double minW, double maxW)
    {
        double clamped = Math.Clamp(requestedW, minW, maxW);
        return (clamped, Math.Abs(clamped - requestedW) > 0.5);
    }

    /// <summary>Duty for fixed-fan mode: 0 stays 0 (stop); 1..min-1 rounds up to the hardware minimum.</summary>
    public static uint NormalizeFixedFanDuty(uint requested, uint minSpinDuty)
    {
        if (requested == 0)
        {
            return 0;
        }

        return Math.Min(Math.Max(requested, minSpinDuty), 100);
    }
}

public sealed record KnobResult(string Knob, bool Applied, string Detail)
{
    public static KnobResult Ok(string knob, string detail = "") => new(knob, true, detail);

    public static KnobResult Fail(string knob, string detail) => new(knob, false, detail);
}

public sealed record ApplyResult(bool AllSucceeded, IReadOnlyList<KnobResult> Results)
{
    public string Summary => string.Join("; ", Results.Select(r => $"{r.Knob}: {(r.Applied ? "ok" : "FAILED")}{(r.Detail.Length > 0 ? $" ({r.Detail})" : string.Empty)}"));
}

/// <summary>
/// The apply engine for one GPU. Every write is validated against the
/// driver-reported range and applied knob-by-knob with per-knob results.
/// Reads back what the driver allows reading back (offsets, power limit,
/// voltage boost); knobs without a getter (clock lock) are tracked in the
/// applied-state file, since the driver offers no query for them.
/// Fan control is deliberately NOT part of profile apply — it is owned by
/// FanControlService (continuous curves) and the CLI's explicit fan command,
/// so profile switches can't fight the fan service.
/// </summary>
public sealed class GpuTuner
{
    private readonly NvmlDevice _nvml;
    private readonly NvapiGpu? _nvapi;
    private readonly object _applyLock = new();
    private uint? _appliedLockMHz;

    public TuningCapabilities Capabilities { get; }

    private readonly string? _gpuUuid;

    public GpuTuner(NvmlDevice nvml, NvapiGpu? nvapi)
    {
        _nvml = nvml;
        _nvapi = nvapi;
        _gpuUuid = nvml.GetUuid();
        Capabilities = DiscoverCapabilities();

        // Restore the tracked lock only when the persisted record belongs to
        // THIS GPU (or predates UUID stamping) — on a multi-GPU system, a lock
        // applied to another card must not be adopted here.
        var state = AppliedStateStore.Load(_gpuUuid);
        if (state is not null && (state.GpuUuid is null || state.GpuUuid == _gpuUuid))
        {
            _appliedLockMHz = state.LockedCoreClockMHz;
        }
    }

    /// <summary>NVML UUID of the GPU this tuner drives (null if the driver won't report one).</summary>
    public string? GpuUuid => _gpuUuid;

    /// <summary>
    /// The clock lock Afterglow last applied (null = none). NVML has no getter
    /// for locked clocks, so this is Afterglow-tracked state, persisted across
    /// restarts via the applied-state file.
    /// </summary>
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

    private TuningCapabilities DiscoverCapabilities()
    {
        bool coreOffset = _nvml.TryGetClockOffset(NvmlClockType.Graphics, out var core) == NvmlReturn.Success;
        bool memOffset = _nvml.TryGetClockOffset(NvmlClockType.Mem, out var mem) == NvmlReturn.Success;

        bool powerLimit = _nvml.TryGetPowerLimitConstraints(out uint minMw, out uint maxMw) == NvmlReturn.Success;
        _ = _nvml.TryGetDefaultPowerLimit(out uint defMw);

        _ = _nvml.TryGetMaxClock(NvmlClockType.Graphics, out uint maxClock);

        uint fanCount = 0;
        uint fanMin = 30;
        bool fanControl = false;
        if (_nvml.TryGetNumFans(out uint fans) == NvmlReturn.Success && fans > 0)
        {
            fanCount = fans;
            _ = _nvml.TryGetMinMaxFanSpeed(out fanMin, out _);
            fanControl = true;
        }

        bool voltageBoost = _nvapi is not null &&
            _nvapi.TryGetVoltageBoostPercent(out _) == NvapiStatus.Ok;

        bool tempLimit = false;
        int tMin = 0, tDef = 0, tMax = 0;
        if (_nvapi is not null &&
            _nvapi.TryGetTempLimitRange(out tMin, out tDef, out tMax) == NvapiStatus.Ok &&
            tMax > tMin)
        {
            tempLimit = true;
        }

        return new TuningCapabilities
        {
            SupportsCoreOffset = coreOffset,
            CoreOffsetMinMHz = core.MinClockOffsetMHz,
            CoreOffsetMaxMHz = core.MaxClockOffsetMHz,
            SupportsMemOffset = memOffset,
            MemOffsetMinMHz = mem.MinClockOffsetMHz,
            MemOffsetMaxMHz = mem.MaxClockOffsetMHz,
            SupportsPowerLimit = powerLimit,
            PowerLimitMinW = minMw / 1000.0,
            PowerLimitMaxW = maxMw / 1000.0,
            PowerLimitDefaultW = defMw / 1000.0,
            SupportsLockedCoreClock = maxClock > 0,
            MaxCoreClockMHz = maxClock,
            SupportsFanControl = fanControl,
            FanCount = fanCount,
            FanMinDutyPct = fanMin,
            SupportsVoltageBoost = voltageBoost,
            SupportsTempLimit = tempLimit,
            TempLimitMinC = tMin,
            TempLimitMaxC = tMax,
            TempLimitDefaultC = tDef,
        };
    }

    /// <summary>Reads the currently applied values (lock is Afterglow-tracked; see <see cref="AppliedLockMHz"/>).</summary>
    public (int CoreOffsetMHz, int MemOffsetMHz, double PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz) ReadCurrent()
    {
        int core = 0, mem = 0;
        if (_nvml.TryGetClockOffset(NvmlClockType.Graphics, out var c) == NvmlReturn.Success)
        {
            core = c.ClockOffsetMHz;
        }

        if (_nvml.TryGetClockOffset(NvmlClockType.Mem, out var m) == NvmlReturn.Success)
        {
            mem = m.ClockOffsetMHz;
        }

        _ = _nvml.TryGetEnforcedPowerLimit(out uint mw);

        uint? boost = null;
        if (_nvapi is not null && _nvapi.TryGetVoltageBoostPercent(out uint b) == NvapiStatus.Ok)
        {
            boost = b;
        }

        return (core, mem, mw / 1000.0, boost, AppliedLockMHz);
    }

    /// <summary>
    /// Applies a profile's clock/power/voltage knobs. A profile with
    /// LockedCoreClockMHz = null removes any active lock, and that removal is
    /// reported as its own knob result so it never happens invisibly.
    /// </summary>
    public ApplyResult Apply(TuningProfile profile)
    {
        lock (_applyLock)
        {
            var results = new List<KnobResult>();

            if (profile.Validate() is string error)
            {
                results.Add(KnobResult.Fail("profile", error));
                return new ApplyResult(false, results);
            }

            // A profile stamped with another card's identity must never land
            // here — same clocks mean different things on different silicon.
            if (profile.GpuUuid is { } target && _gpuUuid is { } mine &&
                !string.Equals(target, mine, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(KnobResult.Fail("profile",
                    $"saved for a different GPU ({profile.GpuName ?? target}) — re-save it on this card to use it here"));
                return new ApplyResult(false, results);
            }

            AppliedStateStore.RecordPending(profile.Name, _gpuUuid);

            ApplyPowerLimit(profile, results);
            ApplyTempLimit(profile, results);
            ApplyVoltageBoost(profile, results);
            ApplyOffset(NvmlClockType.Mem, profile.MemOffsetMHz,
                Capabilities.SupportsMemOffset, Capabilities.MemOffsetMinMHz, Capabilities.MemOffsetMaxMHz,
                "memory offset", results);
            ApplyOffset(NvmlClockType.Graphics, profile.CoreOffsetMHz,
                Capabilities.SupportsCoreOffset, Capabilities.CoreOffsetMinMHz, Capabilities.CoreOffsetMaxMHz,
                "core offset", results);
            ApplyLockedClock(profile.LockedCoreClockMHz, results);

            bool all = results.All(r => r.Applied);
            AppliedStateStore.Record(profile, all, _appliedLockMHz, _gpuUuid);
            Log.Info($"Apply '{profile.Name}': {(all ? "ok" : "PARTIAL")} — {string.Join("; ", results.Select(r => $"{r.Knob}={(r.Applied ? "ok" : "fail")}"))}");
            return new ApplyResult(all, results);
        }
    }

    /// <summary>Returns every knob this engine owns to driver defaults.</summary>
    public ApplyResult ResetToDefaults()
    {
        lock (_applyLock)
        {
            var results = new List<KnobResult>();

            if (Capabilities.SupportsCoreOffset)
            {
                Report(results, "core offset", _nvml.TrySetClockOffset(NvmlClockType.Graphics, 0), "0 MHz");
            }

            if (Capabilities.SupportsMemOffset)
            {
                Report(results, "memory offset", _nvml.TrySetClockOffset(NvmlClockType.Mem, 0), "0 MHz");
            }

            var unlockRc = _nvml.TryResetGpuLockedClocks();
            if (unlockRc == NvmlReturn.Success)
            {
                _appliedLockMHz = null;
            }

            if (unlockRc is not NvmlReturn.NotSupported and not NvmlReturn.FunctionNotFound)
            {
                Report(results, "clock lock", unlockRc, "unlocked");
            }

            if (Capabilities.SupportsPowerLimit && Capabilities.PowerLimitDefaultW > 0)
            {
                Report(results, "power limit",
                    _nvml.TrySetPowerLimit((uint)(Capabilities.PowerLimitDefaultW * 1000)),
                    $"{Capabilities.PowerLimitDefaultW:F0} W");
            }

            if (Capabilities.SupportsTempLimit && _nvapi is not null)
            {
                ReportNv(results, "temp limit", _nvapi.TrySetTempLimit(Capabilities.TempLimitDefaultC),
                    $"{Capabilities.TempLimitDefaultC} C");
            }

            if (Capabilities.SupportsVoltageBoost && _nvapi is not null)
            {
                ReportNv(results, "voltage boost", _nvapi.TrySetVoltageBoostPercent(0), "0%");
            }

            RestoreAutoFans(results);

            bool all = results.All(r => r.Applied);
            AppliedStateStore.Clear(_gpuUuid);
            Log.Info($"Reset to defaults: {(all ? "ok" : "PARTIAL")}");
            return new ApplyResult(all, results);
        }
    }

    private void ApplyOffset(
        NvmlClockType type, int requested, bool supported, int min, int max, string knob, List<KnobResult> results)
    {
        if (requested == 0 && !supported)
        {
            return;
        }

        if (!supported)
        {
            results.Add(KnobResult.Fail(knob, "not supported by this GPU/driver"));
            return;
        }

        var (clamped, wasClamped) = TuningMath.ClampOffset(requested, min, max);
        string detail = wasClamped
            ? $"{clamped} MHz (requested {requested}, clamped to driver range {min}..{max})"
            : $"{clamped} MHz";

        var rc = _nvml.TrySetClockOffset(type, clamped);
        if (rc == NvmlReturn.Success && _nvml.TryGetClockOffset(type, out var readback) == NvmlReturn.Success)
        {
            if (readback.ClockOffsetMHz != clamped)
            {
                results.Add(KnobResult.Fail(knob, $"driver accepted the call but readback shows {readback.ClockOffsetMHz} MHz"));
                return;
            }

            // "(verified)" only when the readback actually happened and matched.
            detail += " (verified)";
        }

        Report(results, knob, rc, detail);
    }

    private void ApplyPowerLimit(TuningProfile profile, List<KnobResult> results)
    {
        if (profile.PowerLimitW is not double watts)
        {
            return;
        }

        if (!Capabilities.SupportsPowerLimit)
        {
            results.Add(KnobResult.Fail("power limit", "not supported"));
            return;
        }

        var (clamped, wasClamped) = TuningMath.ClampPower(watts, Capabilities.PowerLimitMinW, Capabilities.PowerLimitMaxW);
        string detail = wasClamped
            ? $"{clamped:F0} W (requested {watts:F0}, clamped to {Capabilities.PowerLimitMinW:F0}..{Capabilities.PowerLimitMaxW:F0})"
            : $"{clamped:F0} W";

        var rc = _nvml.TrySetPowerLimit((uint)(clamped * 1000));
        if (rc == NvmlReturn.Success &&
            _nvml.TryGetEnforcedPowerLimit(out uint enforcedMw) == NvmlReturn.Success)
        {
            double enforcedW = enforcedMw / 1000.0;
            if (Math.Abs(enforcedW - clamped) > 1.5)
            {
                results.Add(KnobResult.Fail("power limit",
                    $"driver accepted the call but enforces {enforcedW:F0} W"));
                return;
            }

            detail += " (verified)";
        }

        Report(results, "power limit", rc, detail);
    }

    private void ApplyTempLimit(TuningProfile profile, List<KnobResult> results)
    {
        if (profile.TempLimitC is not uint tempC)
        {
            return;
        }

        if (!Capabilities.SupportsTempLimit || _nvapi is null)
        {
            results.Add(KnobResult.Fail("temp limit", "not supported on this GPU/driver"));
            return;
        }

        int clamped = Math.Clamp((int)tempC, Capabilities.TempLimitMinC, Capabilities.TempLimitMaxC);
        ReportNv(results, "temp limit", _nvapi.TrySetTempLimit(clamped), $"{clamped} C");
    }

    private void ApplyVoltageBoost(TuningProfile profile, List<KnobResult> results)
    {
        if (profile.VoltageBoostPct is not uint pct)
        {
            return;
        }

        if (!Capabilities.SupportsVoltageBoost || _nvapi is null)
        {
            results.Add(KnobResult.Fail("voltage boost", "not supported"));
            return;
        }

        uint target = Math.Min(pct, 100);
        var rc = _nvapi.TrySetVoltageBoostPercent(target);
        string detail = $"{target}%";
        if (rc == NvapiStatus.Ok &&
            _nvapi.TryGetVoltageBoostPercent(out uint readback) == NvapiStatus.Ok)
        {
            if (readback != target)
            {
                results.Add(KnobResult.Fail("voltage boost",
                    $"driver accepted the call but readback shows {readback}%"));
                return;
            }

            detail += " (verified)";
        }

        ReportNv(results, "voltage boost", rc, detail);
    }

    /// <summary>
    /// Idle floor for range locks. A P8-style value, not driver-derived — NVML
    /// exposes no queryable minimum. If a GPU rejects it, the failure message
    /// says so explicitly instead of failing opaquely.
    /// </summary>
    private const uint RangeLockFloorMHz = 210;

    private void ApplyLockedClock(uint? target, List<KnobResult> results)
    {
        if (target is uint lockMHz)
        {
            // Allow idle downclocking: lock the range from the idle floor to the target.
            var rc = _nvml.TrySetGpuLockedClocks(RangeLockFloorMHz, lockMHz);
            if (rc == NvmlReturn.Success)
            {
                _appliedLockMHz = lockMHz;
            }

            string detail = rc == NvmlReturn.InvalidArgument
                ? $"{RangeLockFloorMHz}..{lockMHz} MHz — the driver rejected this range; this GPU may " +
                  $"require a higher idle floor than {RangeLockFloorMHz} MHz (please report your model)"
                : $"{RangeLockFloorMHz}..{lockMHz} MHz (Afterglow-tracked; driver has no getter)";
            Report(results, "locked core clock", rc, detail);
            return;
        }

        // Profile carries no lock: release any active one — visibly, never silently.
        if (_appliedLockMHz is uint previous)
        {
            var rc = _nvml.TryResetGpuLockedClocks();
            if (rc == NvmlReturn.Success)
            {
                _appliedLockMHz = null;
            }

            Report(results, "clock lock", rc, $"removed (was {RangeLockFloorMHz}..{previous} MHz)");
        }
    }

    /// <summary>
    /// Re-applies the tuning-style RANGE lock (idle floor .. target) — the form
    /// profiles use, which still allows idle downclocking. The probe restore
    /// path must use this, not <see cref="LockClockForProbe"/>: restoring a
    /// range lock as an exact pin would hold the GPU at full clocks at idle.
    /// </summary>
    public NvmlReturn RestoreTuningLock(uint lockMHz)
    {
        lock (_applyLock)
        {
            var rc = _nvml.TrySetGpuLockedClocks(RangeLockFloorMHz, lockMHz);
            if (rc == NvmlReturn.Success)
            {
                _appliedLockMHz = lockMHz;
            }

            return rc;
        }
    }

    /// <summary>
    /// Pins the core clock to an exact frequency for V/F curve probing (both ends
    /// of the range equal, unlike the tuning lock which allows idle downclocking).
    /// </summary>
    public NvmlReturn LockClockForProbe(uint clockMHz)
    {
        lock (_applyLock)
        {
            var rc = _nvml.TrySetGpuLockedClocks(clockMHz, clockMHz);
            if (rc == NvmlReturn.Success)
            {
                _appliedLockMHz = clockMHz;
            }

            return rc;
        }
    }

    /// <summary>
    /// Explicitly releases any driver-level clock lock, even when Afterglow has no
    /// record of one (a lock can outlive a crashed session until reboot).
    /// </summary>
    public KnobResult ForceUnlock()
    {
        lock (_applyLock)
        {
            var rc = _nvml.TryResetGpuLockedClocks();
            if (rc == NvmlReturn.Success)
            {
                _appliedLockMHz = null;
            }

            return rc == NvmlReturn.Success
                ? KnobResult.Ok("clock lock", "released (explicit)")
                : KnobResult.Fail("clock lock", rc == NvmlReturn.NoPermission
                    ? "needs administrator rights"
                    : rc.ToString());
        }
    }

    /// <summary>Sets all fans to a duty; NVAPI preferred (supports 0%), NVML fallback.</summary>
    public NvmlReturn SetAllFansRaw(uint dutyPct)
    {
        if (_nvapi is not null && _nvapi.TrySetAllFans(dutyPct) == NvapiStatus.Ok)
        {
            return NvmlReturn.Success;
        }

        if (_nvml.TryGetNumFans(out uint fans) != NvmlReturn.Success)
        {
            return NvmlReturn.NotSupported;
        }

        var worst = NvmlReturn.Success;
        for (uint f = 0; f < fans; f++)
        {
            var rc = _nvml.TrySetFanSpeed(f, dutyPct == 0 ? 0 : Math.Clamp(dutyPct, Capabilities.FanMinDutyPct, 100));
            if (rc != NvmlReturn.Success)
            {
                worst = rc;
            }
        }

        return worst;
    }

    /// <summary>Sets one fan (by NVAPI cooler id) to a manual duty.</summary>
    public NvmlReturn SetFanRaw(uint coolerId, uint dutyPct)
    {
        if (_nvapi is not null && _nvapi.TrySetFan(coolerId, dutyPct) == NvapiStatus.Ok)
        {
            return NvmlReturn.Success;
        }

        return NvmlReturn.NotSupported;
    }

    /// <summary>Returns fans to firmware control; NVAPI preferred, NVML fallback.</summary>
    public NvmlReturn RestoreAutoFansRaw()
    {
        if (_nvapi is not null && _nvapi.TryRestoreAutoFans() == NvapiStatus.Ok)
        {
            return NvmlReturn.Success;
        }

        if (_nvml.TryGetNumFans(out uint fans) != NvmlReturn.Success)
        {
            return NvmlReturn.NotSupported;
        }

        var worst = NvmlReturn.Success;
        for (uint f = 0; f < fans; f++)
        {
            var rc = _nvml.TrySetDefaultFanSpeed(f);
            if (rc != NvmlReturn.Success)
            {
                worst = rc;
            }
        }

        return worst;
    }

    private void RestoreAutoFans(List<KnobResult> results)
    {
        var rc = RestoreAutoFansRaw();
        if (rc is not NvmlReturn.NotSupported)
        {
            Report(results, "fans", rc, "auto");
        }
    }

    private static void Report(List<KnobResult> results, string knob, NvmlReturn rc, string detail)
    {
        results.Add(rc == NvmlReturn.Success
            ? KnobResult.Ok(knob, detail)
            : KnobResult.Fail(knob, rc == NvmlReturn.NoPermission
                ? "needs administrator rights"
                : rc.ToString()));
    }

    private static void ReportNv(List<KnobResult> results, string knob, NvapiStatus rc, string detail)
    {
        results.Add(rc == NvapiStatus.Ok
            ? KnobResult.Ok(knob, detail)
            : KnobResult.Fail(knob, rc.ToString()));
    }
}

/// <summary>
/// Persists what was applied so an unclean shutdown (crash, TDR, power cut) can be
/// detected on the next start, and so Afterglow-tracked state (the clock lock, manual
/// fan control) survives restarts. A pending marker is written before an apply begins,
/// so even a crash mid-apply is caught. One file per GPU (keyed by NVML UUID) so two
/// cards never overwrite each other's record; the pre-multi-GPU single file remains
/// readable as the legacy fallback and is retired the first time that GPU's state is
/// written or cleared.
/// </summary>
public static class AppliedStateStore
{
    public sealed record AppliedState(
        string ProfileName,
        DateTimeOffset AppliedAt,
        bool AllKnobsSucceeded,
        bool CleanShutdown,
        uint? LockedCoreClockMHz = null,
        string? FanMode = null,
        uint? FanDuty = null,
        bool Pending = false,
        string? GpuUuid = null);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Lock = new();

    public static void RecordPending(string profileName, string? gpuUuid = null)
    {
        Mutate(gpuUuid, state => (state ?? Empty(profileName)) with
        {
            ProfileName = profileName,
            AppliedAt = DateTimeOffset.Now,
            AllKnobsSucceeded = false,
            CleanShutdown = false,
            Pending = true,
            GpuUuid = gpuUuid,
        });
    }

    public static void Record(TuningProfile profile, bool allSucceeded, uint? lockedClock, string? gpuUuid = null)
    {
        Mutate(gpuUuid, state => (state ?? Empty(profile.Name)) with
        {
            ProfileName = profile.Name,
            AppliedAt = DateTimeOffset.Now,
            AllKnobsSucceeded = allSucceeded,
            CleanShutdown = false,
            LockedCoreClockMHz = lockedClock,
            Pending = false,
            GpuUuid = gpuUuid,
        });
    }

    /// <summary>Records that Afterglow took manual control of the fans (or released it with null).</summary>
    public static void RecordFans(string? mode, uint? duty, string? gpuUuid = null)
    {
        Mutate(gpuUuid, state =>
        {
            if (state is null && mode is null)
            {
                return null; // nothing applied, nothing to record
            }

            return (state ?? Empty("fan control")) with
            {
                FanMode = mode,
                FanDuty = duty,
                CleanShutdown = false,
                GpuUuid = gpuUuid ?? state?.GpuUuid,
            };
        });
    }

    /// <summary>App-level: marks every GPU's record (and the legacy file) as cleanly shut down.</summary>
    public static void MarkCleanShutdown()
    {
        lock (Lock)
        {
            foreach (string path in AllFiles())
            {
                try
                {
                    if (ReadFile(path) is { CleanShutdown: false } state)
                    {
                        WriteFile(path, state with { CleanShutdown = true });
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// State for one GPU: its own file first, else the legacy single file
    /// (whose record predates per-GPU files — callers already gate adoption
    /// on the stamped UUID matching).
    /// </summary>
    public static AppliedState? Load(string? gpuUuid = null)
    {
        lock (Lock)
        {
            if (gpuUuid is not null && ReadFile(PathFor(gpuUuid)) is { } perGpu)
            {
                return perGpu;
            }

            return ReadFile(AppPaths.AppliedStateFile);
        }
    }

    /// <summary>
    /// Every persisted record, for startup crash scanning — per-GPU files plus
    /// the legacy file when no per-GPU file has superseded it (same UUID).
    /// </summary>
    public static IReadOnlyList<AppliedState> LoadAll()
    {
        lock (Lock)
        {
            var states = new List<AppliedState>();
            foreach (string path in PerGpuFiles())
            {
                if (ReadFile(path) is { } state)
                {
                    states.Add(state);
                }
            }

            if (ReadFile(AppPaths.AppliedStateFile) is { } legacy &&
                !states.Any(s => s.GpuUuid is not null && s.GpuUuid == legacy.GpuUuid))
            {
                states.Add(legacy);
            }

            return states;
        }
    }

    /// <summary>Removes the GPU's record — its own file and, if it owns it, the legacy file.</summary>
    public static void Clear(string? gpuUuid = null)
    {
        lock (Lock)
        {
            try
            {
                if (gpuUuid is not null)
                {
                    File.Delete(PathFor(gpuUuid));
                }

                var legacy = ReadFile(AppPaths.AppliedStateFile);
                if (legacy is null || legacy.GpuUuid is null || legacy.GpuUuid == gpuUuid || gpuUuid is null)
                {
                    File.Delete(AppPaths.AppliedStateFile);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Per-GPU file name derived from the NVML UUID ("GPU-2b6ae74e-…" → stable suffix).</summary>
    public static string PathFor(string gpuUuid)
    {
        var keep = new string(gpuUuid.Where(char.IsLetterOrDigit).ToArray());
        if (keep.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
        {
            keep = keep[3..];
        }

        string suffix = keep.Length > 0 ? keep[..Math.Min(12, keep.Length)].ToLowerInvariant() : "unknown";
        return Path.Combine(
            Path.GetDirectoryName(AppPaths.AppliedStateFile)!,
            $"applied-state-{suffix}.json");
    }

    private static IEnumerable<string> PerGpuFiles()
    {
        string dir = Path.GetDirectoryName(AppPaths.AppliedStateFile)!;
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(dir, "applied-state-*.json"))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> AllFiles()
    {
        foreach (string path in PerGpuFiles())
        {
            yield return path;
        }

        yield return AppPaths.AppliedStateFile;
    }

    private static AppliedState? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AppliedState>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteFile(string path, AppliedState state)
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static AppliedState Empty(string name) =>
        new(name, DateTimeOffset.Now, false, false);

    private static void Mutate(string? gpuUuid, Func<AppliedState?, AppliedState?> mutate)
    {
        lock (Lock)
        {
            try
            {
                // Seed the mutation from this GPU's current view (its file, or
                // the legacy file it hasn't superseded yet), but always write
                // to the per-GPU file once a UUID is known.
                var next = mutate(Load(gpuUuid));
                if (next is null)
                {
                    return;
                }

                WriteFile(gpuUuid is not null ? PathFor(gpuUuid) : AppPaths.AppliedStateFile, next);

                // The legacy file is superseded for this GPU from now on;
                // leaving a stale copy would double-report crashes.
                if (gpuUuid is not null &&
                    ReadFile(AppPaths.AppliedStateFile) is { } legacy &&
                    (legacy.GpuUuid is null || legacy.GpuUuid == gpuUuid))
                {
                    File.Delete(AppPaths.AppliedStateFile);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // State tracking must never break an apply.
            }
        }
    }
}
