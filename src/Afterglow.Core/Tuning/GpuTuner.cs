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

    /// <summary>
    /// Driver-reported floor for the clock lock/clamp, where one exists (Intel
    /// frequency domains report theirs; NVML exposes none — 0 means unknown).
    /// Omitted from JSON at 0 so NVIDIA machine-readable output is unchanged.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public uint LockClockMinMHz { get; init; }

    public bool SupportsFanControl { get; init; }
    public uint FanCount { get; init; }
    public uint FanMinDutyPct { get; init; }

    public bool SupportsVoltageBoost { get; init; }

    public bool SupportsTempLimit { get; init; }
    public int TempLimitMinC { get; init; }
    public int TempLimitMaxC { get; init; }
    public int TempLimitDefaultC { get; init; }

    /// <summary>
    /// Per-point V/F curve offsets (the Afterburner-style curve editor),
    /// probed live rather than assumed by generation. Verified working on
    /// RTX 5090 (driver 616.56); expected across Turing→Blackwell.
    /// </summary>
    public bool SupportsVfPoints { get; init; }
    public int VfPointCount { get; init; }
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

    /// <summary>
    /// Spin floor used when the driver will not state one. NVML documents the
    /// minimum as typically 30–100 % (docs/research/driver-apis.md), and the
    /// RTX 5090 reports exactly 30.
    /// </summary>
    public const uint FallbackMinSpinDutyPct = 30;

    /// <summary>
    /// The fixed-fan spin floor from the driver's min/max query (F-22). The
    /// query's out parameter is written even when it fails, so taking it as-is
    /// turned "unsupported" into a 0 % floor: a requested 5 % then reached the
    /// fans unrounded and could stall them in manual mode. Only a successful,
    /// plausible report is trusted; anything else keeps the conservative floor.
    /// A reported 0 is not trusted either — 0 % stays reachable as the explicit
    /// fan-stop request <see cref="NormalizeFixedFanDuty"/> passes through.
    /// </summary>
    public static uint ResolveMinSpinDuty(bool querySucceeded, uint reportedMinPct) =>
        querySucceeded && reportedMinPct is > 0 and <= 100 ? reportedMinPct : FallbackMinSpinDutyPct;

    /// <summary>
    /// A driver-reported default (temp limit, power limit) the reset path may
    /// write back, or null when it is not one (F-24): the reset used to write
    /// the temp-limit default with no check at all, while every other path
    /// clamps that knob into the driver's own range. A default outside the range
    /// the same driver reports — or a zero, the shape of a failed query — is
    /// not something to send to hardware on the panic path.
    /// </summary>
    public static double? UsableDefault(double reportedDefault, double min, double max) =>
        reportedDefault > 0 && min <= max && reportedDefault >= min && reportedDefault <= max
            ? reportedDefault
            : null;
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
public sealed class GpuTuner : IGpuTuner
{
    private readonly INvmlTuningDevice _nvml;
    private readonly INvapiTuningGpu? _nvapi;
    private readonly Lock _applyLock = new();
    private uint? _appliedLockMHz;

    public TuningCapabilities Capabilities { get; }

    private readonly string? _gpuUuid;
    private readonly string _recordKey;

    /// <param name="recordKey">
    /// The card's stable key (see <see cref="IGpuTuner.RecordKey"/>): the UUID,
    /// or the index fallback the GPU manager derives when NVML reports none.
    /// </param>
    public GpuTuner(NvmlDevice nvml, NvapiGpu? nvapi, string recordKey)
        : this((INvmlTuningDevice)nvml, (INvapiTuningGpu?)nvapi, recordKey)
    {
    }

    /// <summary>The same tuner over any driver surface — the fake driver in tests.</summary>
    internal GpuTuner(INvmlTuningDevice nvml, INvapiTuningGpu? nvapi, string recordKey)
    {
        _nvml = nvml;
        _nvapi = nvapi;
        _gpuUuid = nvml.GetUuid();
        _recordKey = recordKey;
        Capabilities = DiscoverCapabilities();

        // The record is this card's own file — adopted from the pre-multi-GPU
        // single file on the first start after an upgrade — so a lock applied
        // to another card can never be adopted here. Nor one applied before
        // the last restart (R-14): the driver dropped it then, and adopting
        // the shadow made `get` report it and a later partial `set` write it
        // back. The record keeps it, still dated to its boot, so every later
        // read refuses it the same way until an apply replaces it.
        if (AppliedStateStore.LoadOrAdoptLegacy(_recordKey, _gpuUuid) is { } state)
        {
            _appliedLockMHz = AppliedStateStore.LockFromThisBoot(state);
            if (state.LockedCoreClockMHz is uint stale && _appliedLockMHz is null)
            {
                Log.Info($"The {stale} MHz clock lock on record for this GPU was applied before the last " +
                         "restart; the driver does not keep a lock across one, so it is not tracked as applied.");
            }
        }
    }

    /// <summary>NVML UUID of the GPU this tuner drives (null if the driver won't report one).</summary>
    public string? GpuUuid => _gpuUuid;

    /// <inheritdoc />
    public string RecordKey => _recordKey;

    /// <summary>
    /// Raised after every call that may have changed what this card runs —
    /// profile apply, reset, per-point writes and clears, clock-lock changes —
    /// whoever made it: a page, a hotkey, the TDR watchdog, the stability
    /// stepper, the certifier, the V/F probe. Pages that display applied state
    /// re-read on it, so a write made in one place never leaves another page
    /// showing a curve or offsets the card no longer has (F-08, F-75).
    /// Raised on the writing thread after the tuner's lock is released;
    /// handlers must not block (marshal to the UI thread asynchronously), and
    /// an exception in one is logged and never reaches the write path.
    /// </summary>
    public event Action<IGpuTuner>? StateWritten;

    private T Notify<T>(T result)
    {
        WriteNotification.Raise(StateWritten, this);
        return result;
    }

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
        uint fanMin = TuningMath.FallbackMinSpinDutyPct;
        bool fanControl = false;
        if (_nvml.TryGetNumFans(out uint fans) == NvmlReturn.Success && fans > 0)
        {
            fanCount = fans;

            // Never `out fanMin` directly: the call writes its out value even on
            // failure, which silently replaced the fallback with 0 (F-22).
            var minRc = _nvml.TryGetMinMaxFanSpeed(out uint reportedMin, out _);
            fanMin = TuningMath.ResolveMinSpinDuty(minRc == NvmlReturn.Success, reportedMin);
            if (minRc != NvmlReturn.Success || fanMin != reportedMin)
            {
                Log.Warn($"Fan minimum duty: driver query returned {minRc} ({reportedMin}%); " +
                         $"using a conservative {fanMin}% spin floor for fixed-duty fan control.");
            }

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

        // Per-point curve control is probed, not assumed by generation — the
        // driver is the authority on whether the interfaces answer.
        int vfPoints = 0;
        if (_nvapi is not null &&
            _nvapi.TryGetVfpPoints(out var vfpProbe) == NvapiStatus.Ok)
        {
            vfPoints = vfpProbe.Count;
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
            SupportsVfPoints = vfPoints > 0,
            VfPointCount = vfPoints,
        };
    }

    /// <summary>Reads the currently applied values (lock is Afterglow-tracked; see <see cref="AppliedLockMHz"/>).</summary>
    public (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz) ReadCurrent()
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

        // A discarded return code published a failed read as a real "0 W"
        // through `get`, `--json` and the MCP state tool. Absent is absent.
        double? powerW = _nvml.TryGetEnforcedPowerLimit(out uint mw) == NvmlReturn.Success
            ? mw / 1000.0
            : null;

        uint? boost = null;
        if (_nvapi is not null && _nvapi.TryGetVoltageBoostPercent(out uint b) == NvapiStatus.Ok)
        {
            boost = b;
        }

        return (core, mem, powerW, boost, AppliedLockMHz);
    }

    /// <summary>
    /// Applies a profile's clock/power/voltage knobs. A profile with
    /// LockedCoreClockMHz = null removes any active lock, and that removal is
    /// reported as its own knob result so it never happens invisibly. The same
    /// rule holds for the per-point V/F curve: a profile carrying no point
    /// offsets means the stock curve. Applying the core offset is what levels
    /// the shared clock-boost table on the hardware this was measured on, so the
    /// reconcile below usually finds nothing to do and stays silent; when it
    /// does have to remove offsets, it says so as its own knob. For the same
    /// reason a profile with no opinion on the curve does not re-write a core
    /// offset the driver already holds (R-12): a partial apply carrying the
    /// current offset forward must not level a curve it never mentioned.
    /// </summary>
    /// <param name="reconcileVfPoints">
    /// Hard opt-out for callers that must never touch the V/F table. Removing a
    /// curve additionally requires the profile itself to have recorded that it
    /// read the table (<see cref="TuningProfile.CapturedVfPoints"/>), so a
    /// profile assembled from <see cref="ReadCurrent"/> — which cannot see
    /// per-point offsets — can never delete a curve the user did not ask to lose.
    /// </param>
    public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true, bool releaseLock = false) =>
        Notify(ApplyUnderLock(profile, reconcileVfPoints, releaseLock));

    private ApplyResult ApplyUnderLock(TuningProfile profile, bool reconcileVfPoints, bool releaseLock)
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

            if (releaseLock && profile.LockedCoreClockMHz is not null)
            {
                results.Add(KnobResult.Fail("profile",
                    $"the profile carries a {profile.LockedCoreClockMHz} MHz clock lock and the lock is to be released " +
                    "— two answers to one question; nothing was applied"));
                return new ApplyResult(false, results);
            }

            AppliedStateStore.RecordPending(profile.Name, _recordKey);

            ApplyPowerLimit(profile, results);
            ApplyTempLimit(profile, results);
            ApplyVoltageBoost(profile, results);
            ApplyOffset(NvmlClockType.Mem, profile.MemOffsetMHz,
                Capabilities.SupportsMemOffset, Capabilities.MemOffsetMinMHz, Capabilities.MemOffsetMaxMHz,
                "memory offset", results);

            // R-12: the core-offset write levels every V/F point (driver-apis.md,
            // Blackwell section), so it is a curve write as much as an offset
            // write. A profile with no opinion on the curve — a power-only `set`
            // or a fan-only MCP apply_tuning carry the current offset forward,
            // as does any profile assembled from ReadCurrent — must not level a
            // curve just to write back the value already in force; the guard
            // below that skips reconciliation for such a profile ran only
            // after the shape was gone. A changed offset is still written: that
            // is the request, and the levelling is the driver's.
            ApplyOffset(NvmlClockType.Graphics, profile.CoreOffsetMHz,
                Capabilities.SupportsCoreOffset, Capabilities.CoreOffsetMinMHz, Capabilities.CoreOffsetMaxMHz,
                "core offset", results, keepWhenUnchanged: !HasCurveOpinion(profile, reconcileVfPoints));
            ApplyLockedClock(profile.LockedCoreClockMHz, results, releaseLock);

            // Runs after the core offset above, on purpose: the global offset
            // lives in the same table and is the baseline this reconciles to.
            ApplyVfPointOffsets(profile, results, reconcileVfPoints);

            bool all = results.All(r => r.Applied);
            AppliedStateStore.Record(profile, all, AppliedLockMHz, _recordKey); // never the probe's pin
            Log.Info($"Apply '{profile.Name}': {(all ? "ok" : "PARTIAL")} — {string.Join("; ", results.Select(r => $"{r.Knob}={(r.Applied ? "ok" : "fail")}"))}");
            return new ApplyResult(all, results);
        }
    }

    // The probe's exact pin while a sweep runs, kept APART from the applied
    // lock: a pin is never "the lock Afterglow applied", and holding it in
    // the same shadow made the next sweep restore a failed-release pin as a
    // range lock the user never set. The applied lock stays what it was, so
    // AppliedLockMHz keeps answering with the user's lock while the pin
    // stands and after a failed pin release — matching the record on disk.
    private uint? _probePinMHz;

    /// <summary>A pin write of the current sweep landed with the driver, so EndProbe has something to release.</summary>
    private bool _probePinLanded;

    private void ClearShadow()
    {
        _appliedLockMHz = null;
        _probePinMHz = null;
    }

    /// <summary>
    /// The one release: reset the driver's locked clocks and, when that lands,
    /// forget the shadow and resolve any V/F probe record for this card. The
    /// Arc tuner funnels its releases through one method too; the three
    /// hand-copied triplets here were where a resolve went missing.
    /// </summary>
    private NvmlReturn ReleaseLockCore()
    {
        var rc = _nvml.TryResetGpuLockedClocks();
        if (rc == NvmlReturn.Success)
        {
            ClearShadow();
            ResolveProbeRecord();
        }

        return rc;
    }

    /// <summary>
    /// A verified lock release or re-apply proves any V/F probe pin on this
    /// card is gone; the store's probe record must say so, or the App raises
    /// "the GPU may still be pinned" on every launch after `set --lock-clock
    /// off` or `reset` freed the card (neither front-end can clear a record
    /// keyed by the index fallback, which only the tuner knows belongs to it).
    /// </summary>
    private void ResolveProbeRecord() => AppliedStateStore.ResolveProbeLock(_recordKey);

    /// <summary>Returns every knob this engine owns to driver defaults.</summary>
    public ApplyResult ResetToDefaults() => Notify(ResetUnderLock());

    private ApplyResult ResetUnderLock()
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

            // Zeroing the whole table is unambiguous here: the global core
            // offset shares it and was just set to 0 above, so the clear can
            // take nothing else with it. Profile apply reaches the same call
            // only for a profile that recorded a curve reading, and always
            // writes the core offset again afterwards. The clear is read back
            // like every other write (F-57): a panic or TDR reset must not
            // report a clear it never confirmed.
            if (Capabilities.SupportsVfPoints && _nvapi is not null)
            {
                results.Add(ClearVfPointsAndVerify());
            }

            var unlockRc = ReleaseLockCore(); // the lock is gone whatever the other knobs do

            if (unlockRc is not NvmlReturn.NotSupported and not NvmlReturn.FunctionNotFound)
            {
                Report(results, "clock lock", unlockRc, "unlocked");
            }

            // Driver-reported defaults are bounded by the same driver's range
            // before they are written (F-24). One that fails the check is
            // skipped and reported — a reset that leaves a knob as it was must
            // say so, not count as a clean reset (the power limit used to be
            // skipped with no result at all when its default read 0).
            if (Capabilities.SupportsPowerLimit)
            {
                if (TuningMath.UsableDefault(
                        Capabilities.PowerLimitDefaultW, Capabilities.PowerLimitMinW, Capabilities.PowerLimitMaxW)
                    is double defaultW)
                {
                    Report(results, "power limit", _nvml.TrySetPowerLimit((uint)(defaultW * 1000)), $"{defaultW:F0} W");
                }
                else
                {
                    results.Add(RefuseDefault("power limit",
                        $"{Capabilities.PowerLimitDefaultW:F0} W",
                        $"{Capabilities.PowerLimitMinW:F0}..{Capabilities.PowerLimitMaxW:F0} W"));
                }
            }

            if (Capabilities.SupportsTempLimit && _nvapi is not null)
            {
                if (TuningMath.UsableDefault(
                        Capabilities.TempLimitDefaultC, Capabilities.TempLimitMinC, Capabilities.TempLimitMaxC)
                    is double defaultC)
                {
                    ReportNv(results, "temp limit", _nvapi.TrySetTempLimit((int)defaultC), $"{defaultC:F0} C");
                }
                else
                {
                    results.Add(RefuseDefault("temp limit",
                        $"{Capabilities.TempLimitDefaultC} C",
                        $"{Capabilities.TempLimitMinC}..{Capabilities.TempLimitMaxC} C"));
                }
            }

            if (Capabilities.SupportsVoltageBoost && _nvapi is not null)
            {
                ReportNv(results, "voltage boost", _nvapi.TrySetVoltageBoostPercent(0), "0%");
            }

            RestoreAutoFans(results);

            bool all = results.All(r => r.Applied);

            // Only a reset that fully landed may erase the applied-state record.
            // Clearing it after a PARTIAL reset threw away the one thing that
            // tells the next launch what is still on this card — the record
            // exists precisely for the case where the driver refused to undo it.
            if (all)
            {
                AppliedStateStore.Clear(_recordKey);
            }

            Log.Info($"Reset to defaults: {(all ? "ok" : "PARTIAL")}");
            return new ApplyResult(all, results);
        }
    }

    /// <summary>A reset step skipped because the driver's own default failed its range check (F-24).</summary>
    private static KnobResult RefuseDefault(string knob, string reportedDefault, string range)
    {
        string detail = $"not reset — the driver-reported default {reportedDefault} is outside its own range " +
                        $"{range} (or was not reported), so it was not written; the current value is unchanged";
        Log.Warn($"Reset to defaults: {knob} {detail}");
        return KnobResult.Fail(knob, detail);
    }

    /// <summary>The driver's stored per-point V/F table with applied offsets (empty when unsupported).</summary>
    public NvapiStatus TryReadVfPoints(out IReadOnlyList<Interop.Nvapi.NvapiGpu.VfpTablePoint> points)
    {
        points = [];
        return _nvapi is null ? NvapiStatus.NoImplementation : _nvapi.TryGetVfpPoints(out points);
    }

    /// <summary>
    /// Writes per-point curve offsets (UI/CLI entry point; serialized with Apply).
    /// Like a profile apply, the write goes on record before it reaches the
    /// driver (R-13), so a crash it causes raises the recovery banner and holds
    /// back the startup profile on the next launch.
    /// </summary>
    public KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex)
    {
        KnobResult result;
        lock (_applyLock)
        {
            result = ApplyVfPointOffsetsCore(offsetsMHzByIndex, recordDirectWrite: true);
        }

        return Notify(result);
    }

    /// <summary>
    /// Confirms a core-offset write by reading it back. False when the readback
    /// disagrees (<paramref name="problem"/> is the sentence to append to the
    /// failure); true when it agrees (<paramref name="problem"/> is "verified")
    /// or the getter did not answer (<paramref name="problem"/> is null: unverified).
    /// <para>
    /// The two V/F-point paths re-write the global core offset after clearing
    /// the table it shares, and reported that re-write from the driver's return
    /// code alone — the exact accept-but-ignore case <see cref="ApplyOffset"/>
    /// guards against a few hundred lines below with "driver accepted the call
    /// but readback shows N MHz". The getter was already in scope in both.
    /// </para>
    /// </summary>
    private bool ConfirmCoreOffset(int expected, out string? problem)
    {
        problem = null;
        if (_nvml.TryGetClockOffset(NvmlClockType.Graphics, out var after) != NvmlReturn.Success)
        {
            // Unverified, not failed — the same rule ApplyOffset follows: it
            // omits "(verified)" when the getter does not answer rather than
            // declaring a write bad. Failing here would report a clear that
            // worked as broken whenever a transient read failed.
            return true;
        }

        if (after.ClockOffsetMHz == expected)
        {
            problem = "verified";
            return true;
        }

        problem = $"but the core offset reads back as {after.ClockOffsetMHz} MHz, not {expected} MHz";
        return false;
    }

    /// <summary>
    /// Clears all per-point curve offsets (UI/CLI entry point), then reads the
    /// table back: the result says whether per-point shape is really gone and
    /// what every point reads afterwards (see <see cref="VfPointPlanner.VerifyClear"/>),
    /// and the global core offset the clear can take with it is written again.
    /// </summary>
    public KnobResult ClearVfPointOffsets()
    {
        KnobResult result;
        lock (_applyLock)
        {
            result = !Capabilities.SupportsVfPoints || _nvapi is null
                ? KnobResult.Fail("V/F points", "per-point curve control is not supported on this GPU/driver")
                : ClearVfPointsKeepingCoreOffset();
        }

        return Notify(result);
    }

    /// <summary>
    /// The manual clear: capture the global core offset BEFORE the clear (the
    /// all-zero table write lands on the same table the offset lives in and can
    /// take it down with the per-point deltas, so reading it afterwards returns
    /// the very 0 we would be trying to undo), clear and verify the table, then
    /// write the captured offset again and confirm it. A getter that does not
    /// answer beforehand means no restore can be promised, and the result says so.
    /// </summary>
    private KnobResult ClearVfPointsKeepingCoreOffset()
    {
        int? offsetBeforeClear = null;
        if (Capabilities.SupportsCoreOffset)
        {
            offsetBeforeClear = _nvml.TryGetClockOffset(NvmlClockType.Graphics, out var before) == NvmlReturn.Success
                ? before.ClockOffsetMHz
                : null;
        }

        var cleared = ClearVfPointsAndVerify();
        if (!cleared.Applied)
        {
            return cleared;
        }

        // A zeroed table reads 0 whether or not the clear took the global
        // offset with it, so no comparison could detect the loss: re-write the
        // pre-clear value unconditionally, exactly as the apply path does.
        string detail = cleared.Detail;
        if (offsetBeforeClear is int restore)
        {
            var reRc = _nvml.TrySetClockOffset(NvmlClockType.Graphics, restore);
            if (reRc != NvmlReturn.Success)
            {
                return KnobResult.Fail(
                    "V/F points",
                    $"{detail}, but the {restore} MHz core offset could not be written again " +
                    $"afterwards ({reRc}) — check it on the Tuning page");
            }

            if (!ConfirmCoreOffset(restore, out string? offsetNote))
            {
                return KnobResult.Fail("V/F points", $"{detail}, {offsetNote} — check it on the Tuning page");
            }

            detail += offsetNote == "verified"
                ? $"; {restore} MHz core offset re-applied (verified)"
                : $"; {restore} MHz core offset re-applied (readback unavailable)";
        }
        else if (Capabilities.SupportsCoreOffset)
        {
            // Never silently write a guessed 0 over a live offset.
            return KnobResult.Fail(
                "V/F points",
                $"{detail}, but the core offset could not be read beforehand, so it was not restored — " +
                "the clear may have zeroed it. Check it on the Tuning page");
        }

        return KnobResult.Ok("V/F points", detail);
    }

    /// <summary>
    /// The clear-all write followed by its readback (F-06/F-43/F-57). The global
    /// core offset is read from the driver after the clear so the report can
    /// say whether the table still carries it — it did on RTX 5090 / 616.56,
    /// and the report states what this card did rather than assuming that.
    /// </summary>
    private KnobResult ClearVfPointsAndVerify()
    {
        var rc = _nvapi!.TryClearVfpPointOffsets();
        if (rc != NvapiStatus.Ok)
        {
            return KnobResult.Fail("V/F points", rc.ToString());
        }

        var readRc = _nvapi.TryGetVfpPoints(out var after);
        int? globalCoreOffset = Capabilities.SupportsCoreOffset &&
            _nvml.TryGetClockOffset(NvmlClockType.Graphics, out var core) == NvmlReturn.Success
                ? core.ClockOffsetMHz
                : null;

        var result = VfPointPlanner.VerifyClear(readRc, after, globalCoreOffset);
        if (!result.Applied)
        {
            Log.Warn($"V/F clear: {result.Detail}");
        }

        return result;
    }

    /// <summary>
    /// Whether applying <paramref name="profile"/> says anything about the V/F
    /// curve — the same two cases <see cref="ApplyVfPointOffsets"/> acts on: it
    /// carries point offsets, or it recorded a table reading and reconciliation
    /// is allowed. Anything else must leave the curve as it is (R-12).
    /// </summary>
    private static bool HasCurveOpinion(TuningProfile profile, bool reconcile) =>
        profile.VfPointOffsetsMHz is { Count: > 0 } || (reconcile && profile.CapturedVfPoints);

    /// <summary>
    /// Reconciles the per-point curve table with what the profile asks for.
    /// A profile carrying no point offsets means the stock curve — not "leave
    /// whatever the last profile flattened in force" — so an earlier flatten is
    /// removed here, visibly, exactly as ApplyLockedClock removes a lock the
    /// profile doesn't carry. Without this, ProfileCertifier would burn, stamp
    /// and mark-stable a profile against a curve it never tested.
    ///
    /// Ordering is load-bearing and measured, not assumed (RTX 5090, driver
    /// 616.56): the global core offset shares this table, and NVML's core-offset
    /// write — issued a few lines above — rewrites every live slot uniformly,
    /// which is what normally erases per-point shape. This step is the check
    /// that it really happened, plus the repair when it didn't.
    /// </summary>
    private void ApplyVfPointOffsets(TuningProfile profile, List<KnobResult> results, bool reconcile)
    {
        const string knob = "V/F points";
        var carried = profile.VfPointOffsetsMHz is { Count: > 0 } ? profile.VfPointOffsetsMHz : null;

        if (!Capabilities.SupportsVfPoints || _nvapi is null)
        {
            // No per-point control: answer only a profile that asked for it, so
            // ordinary profiles on such a GPU gain no spurious failed knob.
            if (carried is not null)
            {
                results.Add(KnobResult.Fail(knob, "per-point curve control is not supported on this GPU/driver"));
            }

            return;
        }

        if (carried is not null)
        {
            results.Add(ApplyVfPointOffsetsCore(carried));
            return;
        }

        // "No point offsets" only means "remove them" when the profile actually
        // looked at the table when it was saved. Anything else has no opinion.
        if (!reconcile || !profile.CapturedVfPoints)
        {
            return;
        }

        // The table is the authority on what is in force. If it can't be read we
        // say nothing about the curve rather than failing a profile whose every
        // real knob landed — the core-offset write above is what normally levels
        // the table, and this step is the backstop that confirms it.
        if (_nvapi.TryGetVfpPoints(out var table) is var readRc && readRc != NvapiStatus.Ok)
        {
            Log.Warn($"Could not read the V/F table after applying '{profile.Name}' ({readRc}); " +
                     "per-point offsets, if any, were neither confirmed nor removed.");
            return;
        }

        if (!VfPointPlanner.HasPerPointShape(table))
        {
            // No per-point shape is in force. On the hardware this was measured
            // on, the core-offset write above is what levels the table, so this
            // is the ordinary outcome and there is nothing left to do or report.
            return;
        }

        // Shape survived the core-offset write. Clear it the way the V/F page
        // does, then verify: a clear that leaves shape behind, or that takes the
        // global core offset with it, must be reported, never assumed away.
        var clearRc = _nvapi.TryClearVfpPointOffsets();
        if (clearRc != NvapiStatus.Ok)
        {
            results.Add(KnobResult.Fail(knob, $"per-point offsets are still in force ({clearRc})"));
            return;
        }

        if (_nvapi.TryGetVfpPoints(out var after) != NvapiStatus.Ok)
        {
            results.Add(KnobResult.Fail(knob, "the removal could not be verified — the table did not read back"));
            return;
        }

        if (VfPointPlanner.HasPerPointShape(after))
        {
            results.Add(KnobResult.Fail(knob, "per-point offsets could not be removed"));
            return;
        }

        // The clear writes zeros across the table the global core offset shares,
        // so the offset is written again unconditionally rather than after a
        // comparison: if the clear took it, the driver now reports 0 and a
        // zeroed table reads 0, so no comparison could have detected the loss.
        // Re-writing the profile's own value is idempotent and costs one call.
        string detail = "per-point offsets removed (this profile recorded none)";
        if (Capabilities.SupportsCoreOffset)
        {
            // Clamp to the driver's range exactly as the offset knob does, and
            // re-use that value for both the write and the readback. Writing the
            // raw profile value and then comparing against it meant a profile
            // carrying an out-of-range offset produced a readback "mismatch"
            // against a value the driver was never going to store — a failure
            // report for a write that landed correctly.
            var (clampedOffset, _) = TuningMath.ClampOffset(
                profile.CoreOffsetMHz, Capabilities.CoreOffsetMinMHz, Capabilities.CoreOffsetMaxMHz);

            var reRc = _nvml.TrySetClockOffset(NvmlClockType.Graphics, clampedOffset);
            if (reRc != NvmlReturn.Success)
            {
                results.Add(KnobResult.Fail(
                    knob,
                    $"{detail}, but the {clampedOffset} MHz core offset could not be written again " +
                    $"afterwards ({reRc}) — check it on the Tuning page"));
                return;
            }

            if (!ConfirmCoreOffset(clampedOffset, out string? offsetNote))
            {
                results.Add(KnobResult.Fail(knob, $"{detail}, {offsetNote} — check it on the Tuning page"));
                return;
            }

            detail += offsetNote == "verified"
                ? $"; core offset re-applied at {clampedOffset} MHz (verified)"
                : $"; core offset re-applied at {clampedOffset} MHz (readback unavailable)";
        }

        results.Add(KnobResult.Ok(knob, detail));
    }

    /// <param name="recordDirectWrite">
    /// True for a write that is not part of a profile apply (which has put
    /// itself on record already): the record is marked pending once the request
    /// has passed validation — a refusal writes nothing and records nothing —
    /// and before the driver call (R-13). A driver refusal leaves it pending:
    /// what the card holds after a refused or half-taken write is not known.
    /// </param>
    private KnobResult ApplyVfPointOffsetsCore(IReadOnlyDictionary<int, int> offsetsMHzByIndex, bool recordDirectWrite = false)
    {
        const string knob = "V/F points";
        if (!Capabilities.SupportsVfPoints || _nvapi is null)
        {
            return KnobResult.Fail(knob, "per-point curve control is not supported on this GPU/driver");
        }

        // Refuse, never reduce (F-10/F-58): the interop clamps to ±1500 MHz and
        // skips slots that are not live core points, both silently. Checking the
        // request against the live table first keeps the write all-or-nothing
        // and leaves no clamp for the readback below to hide behind.
        var preRc = _nvapi.TryGetVfpPoints(out var live);
        if (preRc != NvapiStatus.Ok)
        {
            return KnobResult.Fail(knob, $"the table could not be read before writing ({preRc}) — nothing was written");
        }

        if (VfPointPlanner.ValidatePointOffsets(offsetsMHzByIndex, live.Select(p => p.Index).ToHashSet()) is string refusal)
        {
            return KnobResult.Fail(knob, refusal);
        }

        if (recordDirectWrite)
        {
            AppliedStateStore.RecordPending(AppliedStateStore.VfPointEditName, _recordKey);
        }

        var rc = _nvapi.TrySetVfpPointOffsets(offsetsMHzByIndex);
        if (rc != NvapiStatus.Ok)
        {
            return KnobResult.Fail(knob, rc.ToString());
        }

        // Same bar as every other knob: a write only counts once the readback
        // agrees with what was asked for. Points the driver silently ignored or
        // altered fail the knob loudly.
        var readRc = _nvapi.TryGetVfpPoints(out var readback);
        var result = readRc == NvapiStatus.Ok
            ? VfPointPlanner.VerifyPointWrite(offsetsMHzByIndex, readback)
            : KnobResult.Ok(knob,
                $"{offsetsMHzByIndex.Count} point offsets written — not verified (the table did not read back: {readRc})");

        if (recordDirectWrite)
        {
            // Still unclean: the session that wrote the curve vouches for it at
            // its clean exit, exactly as for an apply.
            AppliedStateStore.RecordDirectWrite(AppliedStateStore.VfPointEditName, result.Applied, _recordKey);
        }

        return result;
    }

    /// <param name="keepWhenUnchanged">
    /// Skip the write when the driver already reports the target (R-12): for the
    /// core offset of a profile with no curve opinion, whose write would level
    /// the per-point curve for nothing. The readback that finds the value in
    /// force is the verification. An unreadable offset is written as before.
    /// </param>
    private void ApplyOffset(
        NvmlClockType type, int requested, bool supported, int min, int max, string knob, List<KnobResult> results,
        bool keepWhenUnchanged = false)
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

        if (keepWhenUnchanged &&
            _nvml.TryGetClockOffset(type, out var inForce) == NvmlReturn.Success &&
            inForce.ClockOffsetMHz == clamped)
        {
            results.Add(KnobResult.Ok(knob, detail + (Capabilities.SupportsVfPoints
                ? " (already in force; not re-written, so any per-point curve stays)"
                : " (already in force; not re-written)")));
            return;
        }

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

    private void ApplyLockedClock(uint? target, List<KnobResult> results, bool releaseLock)
    {
        if (target is uint lockMHz)
        {
            // Allow idle downclocking: lock the range from the idle floor to
            // the target — the same write the probe's restore uses.
            var rc = RestoreTuningLockCore(lockMHz);

            string detail = rc == NvmlReturn.InvalidArgument
                ? $"{RangeLockFloorMHz}..{lockMHz} MHz — the driver rejected this range; this GPU may " +
                  $"require a higher idle floor than {RangeLockFloorMHz} MHz (please report your model)"
                : $"{RangeLockFloorMHz}..{lockMHz} MHz (Afterglow-tracked; driver has no getter)";
            Report(results, "locked core clock", rc, detail);
            return;
        }

        // Profile carries no lock: release any active one — visibly, never
        // silently. An explicit release runs whether or not a lock is tracked:
        // NVML has no getter, so a lock another process wrote is invisible
        // here, and the explicit request must yield exactly one verdict
        // rather than a front-end's reconciliation of two.
        string? releaseDetail = _probePinMHz is uint pin ? $"released the V/F probe's {pin} MHz pin"
            : _appliedLockMHz is uint was ? $"removed (was {RangeLockFloorMHz}..{was} MHz)"
            : releaseLock ? "released (explicit)"
            : null;
        if (releaseDetail is not null)
        {
            Report(results, "clock lock", ReleaseLockCore(), releaseDetail);
        }
    }

    /// <summary>
    /// Re-applies the tuning-style RANGE lock (idle floor .. target) — the form
    /// profiles use, which still allows idle downclocking. The probe restore
    /// path must use this, not <see cref="LockClockForProbe"/>: restoring a
    /// range lock as an exact pin would hold the GPU at full clocks at idle.
    /// </summary>
    public NvmlReturn RestoreTuningLock(uint lockMHz) => Notify(RestoreTuningLockCore(lockMHz));

    /// <summary>
    /// The write behind <see cref="RestoreTuningLock"/>, without the notification:
    /// Apply and EndProbe call it while holding the lock, and their own public
    /// entry points notify once, after the lock is released.
    /// </summary>
    private NvmlReturn RestoreTuningLockCore(uint lockMHz)
    {
        lock (_applyLock)
        {
            var rc = _nvml.TrySetGpuLockedClocks(RangeLockFloorMHz, lockMHz);
            if (rc == NvmlReturn.Success)
            {
                _appliedLockMHz = lockMHz;
                _probePinMHz = null;
                ResolveProbeRecord(); // a range lock this process wrote supersedes any pin
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
        NvmlReturn rc;
        lock (_applyLock)
        {
            // On record BEFORE the write: a pin outlives a killed process, and
            // the record is what the next launch reads. A refused write has
            // pinned nothing, so a record THIS call created is resolved again
            // at once — one an earlier session left is still true and stays.
            bool alreadyPending = AppliedStateStore.RecordProbeLockPending(_recordKey);
            rc = _nvml.TrySetGpuLockedClocks(clockMHz, clockMHz);
            if (rc == NvmlReturn.Success)
            {
                _probePinMHz = clockMHz;
                _probePinLanded = true;
            }
            else if (!alreadyPending)
            {
                ResolveProbeRecord();
            }
        }

        return Notify(rc);
    }

    /// <inheritdoc />
    public ProbeStart BeginProbe()
    {
        lock (_applyLock)
        {
            // NVML has no getter: nothing to release first, and a lock another
            // process wrote is invisible here. The sweep pins over the range
            // lock this process applied, which stays the applied lock
            // throughout, and EndProbe writes it back over the last pin.
            _probePinLanded = false;
            return new ProbeStart(Capabilities.MaxCoreClockMHz, _appliedLockMHz, null);
        }
    }

    /// <inheritdoc />
    public KnobResult EndProbe() => Notify(EndProbeUnderLock());

    private KnobResult EndProbeUnderLock()
    {
        lock (_applyLock)
        {
            // The applied lock as it stands NOW, not as BeginProbe saw it: a
            // lock written mid-sweep (a hotkey's profile) replaced it and a
            // release cleared it, so the write that stopped the sweep is what
            // stays, even under a pin that raced in after it. The Arc tuner,
            // which must keep the displaced lock apart, matches this.
            if (_appliedLockMHz is uint restore)
            {
                var rc = RestoreTuningLockCore(restore);
                return rc == NvmlReturn.Success
                    ? KnobResult.Ok("clock lock", $"restored the {restore} MHz lock")
                    : KnobResult.Fail("clock lock", $"the previous {restore} MHz clock lock could not be restored ({rc})");
            }

            if (!_probePinLanded)
            {
                return KnobResult.Ok("clock lock", "nothing was pinned");
            }

            var release = ReleaseLockCore();
            if (release != NvmlReturn.Success)
            {
                return KnobResult.Fail("clock lock",
                    $"the probe's clock lock could not be released ({(release == NvmlReturn.NoPermission ? "needs administrator rights" : release.ToString())})");
            }

            _probePinLanded = false;
            return KnobResult.Ok("clock lock", "released the probe's pin");
        }
    }

    /// <summary>
    /// Explicitly releases any driver-level clock lock, even when Afterglow has no
    /// record of one (a lock can outlive a crashed session until reboot).
    /// </summary>
    public KnobResult ForceUnlock()
    {
        NvmlReturn rc;
        lock (_applyLock)
        {
            rc = ReleaseLockCore();
        }

        return Notify(rc == NvmlReturn.Success
            ? KnobResult.Ok("clock lock", "released (explicit)")
            : KnobResult.Fail("clock lock", rc == NvmlReturn.NoPermission
                ? "needs administrator rights"
                : rc.ToString()));
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

    /// <summary>
    /// Returns fans to firmware control; NVAPI preferred, NVML fallback.
    /// <see cref="NvmlReturn.NotSupported"/> means there was no fan control to
    /// hand back on either interface — callers (FanControlService.SetAuto, the
    /// reset) count that as released — so it is answered only for statuses that
    /// say the interface is absent (R-08). A lost GPU, a refused call or an
    /// NVAPI release that failed where NVML has no fallback is a failed
    /// release, and is reported as one.
    /// </summary>
    public NvmlReturn RestoreAutoFansRaw()
    {
        NvapiStatus? nvapiRc = _nvapi?.TryRestoreAutoFans();
        if (nvapiRc == NvapiStatus.Ok)
        {
            return NvmlReturn.Success;
        }

        var countRc = _nvml.TryGetNumFans(out uint fans);
        if (countRc is not NvmlReturn.Success and not NvmlReturn.NotSupported and not NvmlReturn.FunctionNotFound)
        {
            Log.Warn($"Fan release: the NVML fallback could not count the fans ({countRc})" +
                     (nvapiRc is { } failed ? $" after NVAPI refused the release ({failed})." : "."));
            return countRc;
        }

        if (countRc != NvmlReturn.Success || fans == 0)
        {
            // NVML has no fans to hand back. That is "nothing to release" only
            // when NVAPI had no fan control either; a real NVAPI failure means
            // the fans may still be manual, with no fallback left to try.
            if (nvapiRc is { } nvapiFailure && !IsAbsent(nvapiFailure))
            {
                Log.Warn($"Fan release failed: NVAPI refused it ({nvapiFailure}) and NVML has no fan control to fall back on.");
                return NvmlReturn.Unknown;
            }

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

    /// <summary>An NVAPI status that says the fan interface is not there, as opposed to a call that failed.</summary>
    private static bool IsAbsent(NvapiStatus status) =>
        status is NvapiStatus.NotSupported or NvapiStatus.NoImplementation or NvapiStatus.FunctionNotFound
            or NvapiStatus.LibraryNotFound or NvapiStatus.ApiNotInitialized;

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

    #region Pre-game snapshot reads (F-21)

    /// <summary>
    /// The temperature limit currently in force, in °C — null when this card
    /// has no temperature-limit control or the driver would not report it.
    /// Read-only: a game rule reads it before its profile lands so the exit
    /// restore can put back the value it found instead of leaving the game's.
    /// </summary>
    public int? ReadTempLimitC()
    {
        if (!Capabilities.SupportsTempLimit || _nvapi is null)
        {
            return null;
        }

        return _nvapi.TryGetTempLimit(out int currentC) == NvapiStatus.Ok ? currentC : null;
    }

    #endregion
}

/// <summary>
/// Persists what was applied so an unclean shutdown (crash, TDR, power cut) can be
/// detected on the next start, and so Afterglow-tracked state (the clock lock, a
/// V/F probe's pin, manual fan control) survives restarts. A pending marker is
/// written before an apply or a pin begins, so even a crash mid-write is caught.
/// One file per card, keyed by the card's stable key — its UUID, or
/// <c>index:N</c> when the driver reports none — and every writer for a card
/// (its tuner, its fan service, a probe pinning it) uses that one key, so two
/// cards never overwrite each other and no record has two homes. The
/// pre-multi-GPU single file is migrated to a card's own file the first time
/// that card's tuner starts (<see cref="LoadOrAdoptLegacy"/>); until then it is
/// only ever listed, so an orphan still raises the banner and can be dismissed.
/// </summary>
public static class AppliedStateStore
{
    /// <summary>
    /// The pending-record name for a V/F probe's pin — the text the next-launch
    /// banner displays for a record the probe created and nothing else wrote.
    /// </summary>
    public const string ProbeLockPendingName = "v/f probe clock lock";

    /// <summary>
    /// The record name for a direct per-point V/F write (the V/F page's point
    /// edits and flatten, <c>vfpoints --set/--flatten</c>) — what the next
    /// launch's banner names when such a write was the last thing applied (R-13).
    /// </summary>
    public const string VfPointEditName = "per-point V/F edit";

    /// <summary>The prefix of the index-fallback key a card without a driver UUID is recorded under.</summary>
    public const string IndexKeyPrefix = "index:";

    /// <summary>The record key of a card the driver gives no UUID: one formula, used by the GPU manager and the tests.</summary>
    public static string IndexKeyFor(uint index) =>
        IndexKeyPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// One card's record. <paramref name="GpuUuid"/> is the key the record is
    /// filed under (a UUID or an index key; the JSON name predates index keys).
    /// <paramref name="LockBoot"/> dates <paramref name="LockedCoreClockMHz"/> to
    /// the OS boot it was written in (R-14); it is written with the lock and
    /// only with it, so no later write of another field can re-date a lock from
    /// an earlier boot. Null on records older than the stamp.
    /// </summary>
    public sealed record AppliedState(
        string ProfileName,
        DateTimeOffset AppliedAt,
        bool AllKnobsSucceeded,
        bool CleanShutdown,
        uint? LockedCoreClockMHz = null,
        string? FanMode = null,
        uint? FanDuty = null,
        string? GpuUuid = null,
        bool ProbeLockPending = false,
        BootStamp? LockBoot = null);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Lock Sync = new();

    public static void RecordPending(string profileName, string key) => RecordUnclean(profileName, false, key);

    /// <summary>
    /// How a direct write that is not a profile apply ended — a per-point V/F
    /// edit (R-13). Its pending mark went on record before the write; the
    /// record stays unclean either way, like an apply's, until the session that
    /// wrote it ends cleanly.
    /// </summary>
    public static void RecordDirectWrite(string name, bool succeeded, string key) => RecordUnclean(name, succeeded, key);

    private static void RecordUnclean(string name, bool allSucceeded, string key)
    {
        Mutate(key, state => (state ?? Empty(name)) with
        {
            ProfileName = name,
            AppliedAt = DateTimeOffset.Now,
            AllKnobsSucceeded = allSucceeded,
            CleanShutdown = false,
            GpuUuid = key,
        });
    }

    /// <summary>
    /// Records an apply. <paramref name="lockWrittenByAfterglow"/> must be a
    /// clock lock this process wrote, or inherited from a crashed session's
    /// record — never one merely observed from the driver, and never a probe's
    /// exact pin. An observed factory ceiling persisted here is loaded by the
    /// next launch as "written by Afterglow", and the release path then refuses
    /// to adopt it forever; a pin persisted here is restored as a range lock
    /// the user never set.
    /// </summary>
    public static void Record(TuningProfile profile, bool allSucceeded, uint? lockWrittenByAfterglow, string key)
    {
        Mutate(key, state => (state ?? Empty(profile.Name)) with
        {
            ProfileName = profile.Name,
            AppliedAt = DateTimeOffset.Now,
            AllKnobsSucceeded = allSucceeded,
            CleanShutdown = false,
            LockedCoreClockMHz = lockWrittenByAfterglow,

            // A lock recorded now is one this boot holds: written by this
            // process, or adopted from a record LockFromThisBoot accepted.
            LockBoot = lockWrittenByAfterglow is null ? null : BootIdentity.Now(),
            GpuUuid = key,
        });
    }

    /// <summary>
    /// The clock lock on <paramref name="state"/> if the driver can still be
    /// holding it — it was recorded in the OS boot running now — else null
    /// (R-14). NVML locks do not survive a restart, and NVML has no getter to
    /// ask. A record older than the boot stamp is judged by its AppliedAt: no
    /// write leaves that earlier than the lock's own, so a record last written
    /// before this boot is refused for certain; one written since is taken as
    /// this boot's, which is as much as an unstamped record can say.
    /// </summary>
    public static uint? LockFromThisBoot(AppliedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.LockedCoreClockMHz is not uint lockMHz)
        {
            return null;
        }

        var now = BootIdentity.Now();
        bool thisBoot = state.LockBoot is { } stamp
            ? BootIdentity.IsSameBoot(stamp, now)
            : BootIdentity.WasWrittenThisBoot(state.AppliedAt, now);
        return thisBoot ? lockMHz : null;
    }

    /// <summary>Records that Afterglow took manual control of the fans (or released it with null).</summary>
    public static void RecordFans(string? mode, uint? duty, string key)
    {
        Mutate(key, state =>
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
                GpuUuid = key,
            };
        });
    }

    /// <summary>
    /// Records that a V/F probe's exact pin is (or may be) on the card. The
    /// tuner writes it BEFORE the pin lands and resolves it on every verified
    /// release or re-apply, so the store reflects the hardware whatever process
    /// or front-end touched it last, and a process killed mid-sweep leaves the
    /// truthful record behind. The flag is its own fact, independent of
    /// <see cref="AppliedState.CleanShutdown"/>: a clean exit is still a clean
    /// exit, and the next launch reads the flag to raise the pin banner.
    /// Returns true when the flag was already set — by an earlier step, or by
    /// a session that never released its pin — so a refused write resolves only
    /// a record it created itself, and a sweep writes the file once, not per step.
    /// </summary>
    public static bool RecordProbeLockPending(string key)
    {
        bool alreadyPending = false;
        Mutate(key, state =>
        {
            if (state is { ProbeLockPending: true })
            {
                alreadyPending = true;
                return null; // already on record: nothing to write, and not this call's to resolve
            }

            return (state ?? Empty(ProbeLockPendingName)) with
            {
                AppliedAt = DateTimeOffset.Now,
                ProbeLockPending = true,
                GpuUuid = key,
            };
        });
        return alreadyPending;
    }

    /// <summary>
    /// The probe's pin on <paramref name="key"/> is verifiably gone. A record
    /// the probe created that nothing else wrote into is dropped outright; any
    /// other record only loses the flag — the rest of it (a tuning lock, fan
    /// control, its shutdown state) is still its owner's to track.
    /// </summary>
    public static void ResolveProbeLock(string key)
    {
        lock (Sync)
        {
            try
            {
                string path = PathFor(key);
                if (ReadFile(path) is not { ProbeLockPending: true } state)
                {
                    return;
                }

                if (IsProbeOnly(state))
                {
                    File.Delete(path);
                }
                else
                {
                    WriteFile(path, state with { ProbeLockPending = false });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// App-level: marks every record (an un-migrated legacy file included) as
    /// cleanly shut down. The probe flag is kept across the mark unless
    /// <paramref name="resolveProbeLocks"/> is true (the user dismissing the
    /// banner) — then it is cleared, and a record that held nothing else is
    /// dropped so no orphan lingers. Only a caller that answers for every card
    /// may use this form; a command that wrote one card uses
    /// <see cref="BeginCommand"/> or the keyed overload (R-15).
    /// </summary>
    public static void MarkCleanShutdown(bool resolveProbeLocks = false)
    {
        lock (Sync)
        {
            foreach (string path in AllFiles())
            {
                MarkClean(path, resolveProbeLocks);
            }
        }
    }

    /// <summary>
    /// Marks only the records filed under <paramref name="keys"/> as cleanly
    /// shut down (R-15) — the cards the caller wrote and answers for. Another
    /// card's record, and a legacy file no tuner has adopted, are left as they
    /// are: they are another session's evidence, and marking them clean hid a
    /// running app's unfinished tuning from the next launch's crash check.
    /// </summary>
    public static void MarkCleanShutdown(IEnumerable<string> keys, bool resolveProbeLocks = false)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (Sync)
        {
            foreach (string path in keys.Select(PathFor).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                MarkClean(path, resolveProbeLocks);
            }
        }
    }

    /// <summary>
    /// A one-shot command's claim on one card's record, taken BEFORE its first
    /// write (R-15). The CLI holds no hardware-owner lock, so the app can be
    /// running beside it with unfinished tuning of its own: a record the
    /// command finds already unclean is that session's — or a crash nobody has
    /// acknowledged yet — and the command's clean end must not erase it. The
    /// command's own write then leaves the record unclean, which overstates
    /// nothing: the evidence it found is still unresolved.
    /// </summary>
    public static CommandRecordClaim BeginCommand(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new CommandRecordClaim(key, foundUnclean: Load(key) is { CleanShutdown: false });
    }

    /// <summary>See <see cref="BeginCommand"/>.</summary>
    public sealed class CommandRecordClaim
    {
        private readonly string _key;
        private readonly bool _foundUnclean;

        internal CommandRecordClaim(string key, bool foundUnclean)
        {
            _key = key;
            _foundUnclean = foundUnclean;
        }

        /// <summary>
        /// The command completed: mark its card's record clean — unless the
        /// record was already unclean when the claim was taken. No other card's
        /// record is touched. Returns whether the mark was made.
        /// </summary>
        public bool MarkClean()
        {
            if (_foundUnclean)
            {
                return false;
            }

            MarkCleanShutdown([_key]);
            return true;
        }
    }

    private static void MarkClean(string path, bool resolveProbeLocks)
    {
        try
        {
            if (ReadFile(path) is not { } state)
            {
                return;
            }

            if (resolveProbeLocks && state.ProbeLockPending && IsProbeOnly(state))
            {
                File.Delete(path);
                return;
            }

            bool clearFlag = resolveProbeLocks && state.ProbeLockPending;
            if (!state.CleanShutdown || clearFlag)
            {
                WriteFile(path, state with
                {
                    CleanShutdown = true,
                    ProbeLockPending = state.ProbeLockPending && !clearFlag,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Test seam: writes the pre-multi-GPU single file the way an older build did.</summary>
    internal static void WriteLegacyRecord(AppliedState state)
    {
        lock (Sync)
        {
            WriteFile(AppPaths.AppliedStateFile, state);
        }
    }

    /// <summary>The record filed under <paramref name="key"/>, or null.</summary>
    public static AppliedState? Load(string key)
    {
        lock (Sync)
        {
            return ReadFile(PathFor(key));
        }
    }

    /// <summary>
    /// NVIDIA GPUs in this process, set by GpuManager before any tuner reads
    /// its state; 0 = unknown (treated like one card, the old behaviour).
    /// </summary>
    public static int DetectedGpuCount { get; set; }

    /// <summary>
    /// A tuner's first read: its own file, after adopting the pre-multi-GPU
    /// single file when that file is this card's. An UNSTAMPED legacy record
    /// predates Arc write support, so it can only have been written for an
    /// NVIDIA card — an Intel identity never adopts it (a hybrid machine
    /// upgrading from an old build would otherwise hand the NVIDIA lock to the
    /// Arc tuner). A record stamped for a different card is not ours to read
    /// and stays where it is. Once adopted, the legacy file is retired.
    /// </summary>
    public static AppliedState? LoadOrAdoptLegacy(string key, string? uuid)
    {
        lock (Sync)
        {
            var own = ReadFile(PathFor(key));
            if (ReadFile(AppPaths.AppliedStateFile) is not { } legacy)
            {
                return own;
            }

            // An UNSTAMPED record says nothing about which card it was. On a
            // one-NVIDIA-card machine that is the single-GPU upgrade and it
            // migrates; with several NVIDIA cards it cannot be every card's, so
            // none adopts it (each would otherwise inherit its tracked clock lock
            // as a phantom lock). LoadAll still lists it, so an unclean one still
            // raises the crash banner and Dismiss clears it (F-59).
            bool ours = legacy.GpuUuid is null
                ? !IsIntelKey(key) && DetectedGpuCount <= 1
                : legacy.GpuUuid == uuid || legacy.GpuUuid == key;
            if (!ours)
            {
                return own;
            }

            try
            {
                // Adopt the legacy record when the card has none of its own; merge
                // it when the card's only record is a probe's — the previous
                // layout kept a UUID-less card's lock in the legacy file while a
                // probe record lived in its index-keyed file, and dropping the
                // legacy file on the strength of that probe record lost the one
                // lock the tuner still had to release. A real record of the
                // card's own wins outright: merging into it would resurrect a
                // lock the user has since released on every launch that found
                // the legacy file still there (a delete that failed).
                if (own is null)
                {
                    own = legacy with { GpuUuid = key };
                    WriteFile(PathFor(key), own);
                }
                else if (IsProbeOnly(own))
                {
                    own = legacy with
                    {
                        GpuUuid = key,
                        ProbeLockPending = true,
                        AppliedAt = own.AppliedAt,
                    };
                    WriteFile(PathFor(key), own);
                }

                File.Delete(AppPaths.AppliedStateFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return own;
        }
    }

    /// <summary>
    /// A record the probe created that nothing else has written into since:
    /// no fan control, no tuning lock. Fans set after a failed probe share the
    /// record and keep its probe name, so the name alone is not enough — the
    /// fan record was being deleted with the flag.
    /// </summary>
    private static bool IsProbeOnly(AppliedState state) =>
        state.ProfileName == ProbeLockPendingName && state.FanMode is null && state.LockedCoreClockMHz is null;

    private static bool IsIntelKey(string key) =>
        key.StartsWith("INTEL-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every persisted record, for startup crash scanning — per-card files plus
    /// a legacy file no tuner has adopted yet (a card that is absent, or an
    /// upgrade whose card has not started a tuner since).
    /// </summary>
    public static IReadOnlyList<AppliedState> LoadAll()
    {
        lock (Sync)
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

    /// <summary>Removes the record filed under <paramref name="key"/>.</summary>
    public static void Clear(string key)
    {
        lock (Sync)
        {
            try
            {
                File.Delete(PathFor(key));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Per-card file name derived from the key ("GPU-2b6ae74e-…",
    /// "INTEL-0000:00:02.0-…" or "index:0" → stable suffix). Vendor prefixes are
    /// stripped so the 12-character budget is spent on the identifying digits.
    /// </summary>
    public static string PathFor(string key)
    {
        var keep = new string(key.Where(char.IsLetterOrDigit).ToArray());
        if (keep.StartsWith("INTEL", StringComparison.OrdinalIgnoreCase))
        {
            keep = "i" + keep[5..]; // keep vendor namespaces disjoint post-strip
        }
        else if (keep.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
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

    private static void Mutate(string key, Func<AppliedState?, AppliedState?> mutate)
    {
        lock (Sync)
        {
            try
            {
                string path = PathFor(key);
                var next = mutate(ReadFile(path));
                if (next is not null)
                {
                    WriteFile(path, next);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // State tracking must never break an apply.
            }
        }
    }
}

/// <summary>
/// Delivers <see cref="GpuTuner.StateWritten"/>. The notification is raised
/// from hardware-write paths — including the TDR watchdog's and the panic
/// hotkey's resets — so one subscriber's failure must neither stop the others
/// nor surface in the code that just wrote the GPU.
/// </summary>
public static class WriteNotification
{
    public static void Raise<T>(Action<T>? handlers, T sender)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(sender);
            }
            catch (Exception ex)
            {
                // Deliberately broad: a display refresh may fail any way it
                // likes; the write already happened and must not be undone by it.
                Log.Error("A tuning-write subscriber failed; the write itself is unaffected.", ex);
            }
        }
    }
}
