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
    /// does have to remove offsets, it says so as its own knob.
    /// </summary>
    /// <param name="reconcileVfPoints">
    /// Hard opt-out for callers that must never touch the V/F table. Removing a
    /// curve additionally requires the profile itself to have recorded that it
    /// read the table (<see cref="TuningProfile.CapturedVfPoints"/>), so a
    /// profile assembled from <see cref="ReadCurrent"/> — which cannot see
    /// per-point offsets — can never delete a curve the user did not ask to lose.
    /// </param>
    public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true)
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

            // Runs after the core offset above, on purpose: the global offset
            // lives in the same table and is the baseline this reconciles to.
            ApplyVfPointOffsets(profile, results, reconcileVfPoints);

            bool all = results.All(r => r.Applied);
            AppliedStateStore.Record(profile, all, _appliedLockMHz, _gpuUuid);
            Log.Info($"Apply '{profile.Name}': {(all ? "ok" : "PARTIAL")} — {string.Join("; ", results.Select(r => $"{r.Knob}={(r.Applied ? "ok" : "fail")}"))}");
            return new ApplyResult(all, results);
        }
    }

    /// <summary>Returns every knob this engine owns to driver defaults.</summary>
    /// <inheritdoc />
    public string? ProbeRecordKey { get; set; }

    /// <summary>
    /// A verified lock release or re-apply proves any V/F probe pin on this
    /// card is gone; the store's probe record must say so, or the App raises
    /// "the GPU may still be pinned" on every launch after `set --lock-clock
    /// off` or `reset` freed the card (neither front-end can clear a record
    /// keyed by the index fallback, which only the tuner knows belongs to it).
    /// </summary>
    private void ResolveProbeRecord()
    {
        if ((ProbeRecordKey ?? _gpuUuid) is { } key)
        {
            AppliedStateStore.ResolveProbeLock(key);
        }
    }

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

            // Zeroing the whole table is unambiguous here: the global core
            // offset shares it and was just set to 0 above, so the clear can
            // take nothing else with it. Profile apply reaches the same call
            // only for a profile that recorded a curve reading, and always
            // writes the core offset again afterwards.
            if (Capabilities.SupportsVfPoints && _nvapi is not null)
            {
                ReportNv(results, "V/F points", _nvapi.TryClearVfpPointOffsets(), "cleared");
            }

            var unlockRc = _nvml.TryResetGpuLockedClocks();
            if (unlockRc == NvmlReturn.Success)
            {
                _appliedLockMHz = null;
                ResolveProbeRecord(); // the lock is gone whatever the other knobs do
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

            // Only a reset that fully landed may erase the applied-state record.
            // Clearing it after a PARTIAL reset threw away the one thing that
            // tells the next launch what is still on this card — the record
            // exists precisely for the case where the driver refused to undo it.
            if (all)
            {
                AppliedStateStore.Clear(_gpuUuid);
            }

            Log.Info($"Reset to defaults: {(all ? "ok" : "PARTIAL")}");
            return new ApplyResult(all, results);
        }
    }

    /// <summary>The driver's stored per-point V/F table with applied offsets (empty when unsupported).</summary>
    public NvapiStatus TryReadVfPoints(out IReadOnlyList<Interop.Nvapi.NvapiGpu.VfpTablePoint> points)
    {
        points = [];
        return _nvapi is null ? NvapiStatus.NoImplementation : _nvapi.TryGetVfpPoints(out points);
    }

    /// <summary>Writes per-point curve offsets (UI/CLI entry point; serialized with Apply).</summary>
    public KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex)
    {
        lock (_applyLock)
        {
            return ApplyVfPointOffsetsCore(offsetsMHzByIndex);
        }
    }

    /// <summary>
    /// Confirms a core-offset write by reading it back. Returns null when the
    /// offset is confirmed, otherwise the sentence to append to the failure.
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

    /// <summary>Clears all per-point curve offsets (UI/CLI entry point).</summary>
    public KnobResult ClearVfPointOffsets()
    {
        lock (_applyLock)
        {
            if (!Capabilities.SupportsVfPoints || _nvapi is null)
            {
                return KnobResult.Fail("V/F points", "per-point curve control is not supported on this GPU/driver");
            }

            // Capture the global core offset BEFORE the clear. The all-zero
            // table write lands on the same table the global offset lives in and
            // can take it down with the per-point deltas, so reading afterwards
            // returns the very 0 we would be trying to undo — restoring it would
            // then print "0 MHz core offset re-applied" over the user's live
            // +150 MHz. A getter that does not answer means we cannot promise a
            // restore, so nothing is re-applied and the result says so.
            int? offsetBeforeClear = null;
            if (Capabilities.SupportsCoreOffset)
            {
                offsetBeforeClear = _nvml.TryGetClockOffset(NvmlClockType.Graphics, out var before) == NvmlReturn.Success
                    ? before.ClockOffsetMHz
                    : null;
            }

            var rc = _nvapi.TryClearVfpPointOffsets();
            if (rc != NvapiStatus.Ok)
            {
                return KnobResult.Fail("V/F points", rc.ToString());
            }

            // Same contract as the profile-apply twin below: a write the driver
            // accepted is not a write that took. Reporting "cleared" off the
            // return code alone meant a clear the driver ignored still printed ok.
            if (_nvapi.TryGetVfpPoints(out var after) != NvapiStatus.Ok)
            {
                return KnobResult.Fail("V/F points", "the removal could not be verified — the table did not read back");
            }

            if (VfPointPlanner.HasPerPointShape(after))
            {
                return KnobResult.Fail("V/F points", "per-point offsets could not be removed");
            }

            // A zeroed table reads 0 whether or not the clear took the global
            // offset with it, so no comparison could detect the loss — re-write
            // the pre-clear value unconditionally, exactly as the apply path does.
            string detail = "cleared (verified)";
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
    }

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

    private KnobResult ApplyVfPointOffsetsCore(IReadOnlyDictionary<int, int> offsetsMHzByIndex)
    {
        const string knob = "V/F points";
        if (!Capabilities.SupportsVfPoints || _nvapi is null)
        {
            return KnobResult.Fail(knob, "per-point curve control is not supported on this GPU/driver");
        }

        var rc = _nvapi.TrySetVfpPointOffsets(offsetsMHzByIndex);
        if (rc != NvapiStatus.Ok)
        {
            return KnobResult.Fail(knob, rc.ToString());
        }

        // Same bar as every other knob: a write only counts once the readback
        // agrees. Points the driver silently ignored fail the knob loudly.
        if (_nvapi.TryGetVfpPoints(out var readback) == NvapiStatus.Ok)
        {
            var byIndex = readback.ToDictionary(p => p.Index, p => p.OffsetMHz);
            int applied = 0;
            foreach (var (index, offset) in offsetsMHzByIndex)
            {
                int expected = Math.Clamp(
                    offset, -Interop.Nvapi.NvapiGpu.VfpOffsetLimitMHz, Interop.Nvapi.NvapiGpu.VfpOffsetLimitMHz);
                if (byIndex.TryGetValue(index, out int actual) && actual == expected)
                {
                    applied++;
                }
            }

            return applied == offsetsMHzByIndex.Count
                ? KnobResult.Ok(knob, $"{applied} point offsets (verified)")
                : KnobResult.Fail(knob,
                    $"driver accepted the write but readback matched only {applied}/{offsetsMHzByIndex.Count} points");
        }

        return KnobResult.Ok(knob, $"{offsetsMHzByIndex.Count} point offsets (readback unavailable)");
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
                ResolveProbeRecord(); // a range lock this process wrote supersedes any probe pin
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
                ResolveProbeRecord();
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
                ResolveProbeRecord();
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
    /// <summary>
    /// The pending-record name for a V/F probe that could not put the clock
    /// state back — the text the next-launch banner displays. One constant, so
    /// the probe, the App and the shutdown sweep cannot drift apart on it.
    /// </summary>
    public const string ProbeLockPendingName = "v/f probe clock lock";

    public sealed record AppliedState(
        string ProfileName,
        DateTimeOffset AppliedAt,
        bool AllKnobsSucceeded,
        bool CleanShutdown,
        uint? LockedCoreClockMHz = null,
        string? FanMode = null,
        uint? FanDuty = null,
        bool Pending = false,
        string? GpuUuid = null,
        bool ProbeLockPending = false);

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
            // A run where NVML won't report a UUID is not evidence the record
            // changed owner — keep a stamp we are in no position to replace.
            GpuUuid = gpuUuid ?? state?.GpuUuid,
        });
    }

    /// <summary>
    /// Records an apply. <paramref name="lockWrittenByAfterglow"/> must be a
    /// clock lock this process wrote, or inherited from a crashed session's
    /// record — never one merely observed from the driver. An observed factory
    /// ceiling persisted here is loaded by the next launch as "written by
    /// Afterglow", and the release path then refuses to adopt it forever. The
    /// NVML tuner's shadow is only ever written by its own calls; a tuner with a
    /// readback getter (Intel) must filter by provenance before calling this.
    /// </summary>
    public static void Record(TuningProfile profile, bool allSucceeded, uint? lockWrittenByAfterglow, string? gpuUuid = null)
    {
        Mutate(gpuUuid, state => (state ?? Empty(profile.Name)) with
        {
            ProfileName = profile.Name,
            AppliedAt = DateTimeOffset.Now,
            AllKnobsSucceeded = allSucceeded,
            CleanShutdown = false,
            LockedCoreClockMHz = lockWrittenByAfterglow,
            Pending = false,
            GpuUuid = gpuUuid ?? state?.GpuUuid, // keep the stamp when this run has no UUID (see RecordPending)
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

    /// <summary>
    /// App-level: marks every GPU's record (and the legacy file) as cleanly shut
    /// down. A record whose V/F probe left a clock pin unresolved (see
    /// <see cref="RecordProbeLockPending"/>) stays unclean — the pin outlives
    /// the process, and the App used to re-record it after this call purely to
    /// undo the mark — unless <paramref name="resolveProbeLocks"/> is true (the
    /// user dismissing the banner).
    /// </summary>
    public static void MarkCleanShutdown(bool resolveProbeLocks = false)
    {
        lock (Lock)
        {
            foreach (string path in AllFiles())
            {
                try
                {
                    if (ReadFile(path) is { CleanShutdown: false } state &&
                        (resolveProbeLocks || !state.ProbeLockPending))
                    {
                        WriteFile(path, state with { CleanShutdown = true, ProbeLockPending = false });
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// State for one GPU: its own file first, else the legacy single file —
    /// but only when that legacy record is this GPU's. An unstamped record
    /// predates per-GPU files and still migrates (the single-GPU upgrade);
    /// a record stamped for a DIFFERENT card is not ours to read. Handing it
    /// back would let Mutate copy the fields a write doesn't touch — the
    /// tracked clock lock above all — into our file under our stamp, which
    /// defeats GpuTuner's identity guard on the next launch.
    ///
    /// Called with a null uuid (a run where NVML would not identify the card)
    /// there is no identity to compare, so the legacy record is returned as-is
    /// and a write keeps whatever stamp it already carried: best effort, and
    /// the only case where this can still hand back another card's record.
    /// </summary>
    public static AppliedState? Load(string? gpuUuid = null)
    {
        lock (Lock)
        {
            if (gpuUuid is not null && ReadFile(PathFor(gpuUuid)) is { } perGpu)
            {
                return perGpu;
            }

            var legacy = ReadFile(AppPaths.AppliedStateFile);
            if (gpuUuid is not null && legacy?.GpuUuid is { } owner && owner != gpuUuid)
            {
                return null; // another card's record: never adopted, never seeded from
            }

            // An UNSTAMPED legacy record predates Arc write support, so it can
            // only have been written for an NVIDIA card — an Intel identity
            // must never adopt it (a hybrid machine upgrading from an old
            // build would otherwise hand the NVIDIA lock to the Arc tuner).
            if (legacy is { GpuUuid: null } && IsIntelUuid(gpuUuid))
            {
                return null;
            }

            return legacy;
        }
    }

    /// <summary>
    /// True for the index fallback a card without a driver UUID is recorded
    /// under (<c>index:N</c>) — a key no tuner ever loads or clears.
    /// </summary>
    public static bool IsIndexKey(string? key) =>
        key is not null && key.StartsWith("index:", StringComparison.Ordinal);

    /// <summary>
    /// Records that a V/F probe left a clock pin it could not release on the
    /// card <paramref name="key"/> (UUID or index fallback). An index key
    /// touches only its own file: routing it through <see cref="RecordPending"/>
    /// seeded the new file from the unstamped legacy record and retired that
    /// record — the ONLY file a UUID-less NVIDIA tuner reads and writes — so the
    /// tuner's tracked lock migrated to a file it never loads.
    /// </summary>
    public static void RecordProbeLockPending(string key)
    {
        if (!IsIndexKey(key))
        {
            // A real UUID owns its per-GPU file (and a legacy record stamped
            // with it); the ordinary write path is the right one for it.
            Mutate(key, state => (state ?? Empty(ProbeLockPendingName)) with
            {
                AppliedAt = DateTimeOffset.Now,
                CleanShutdown = false,
                ProbeLockPending = true,
                GpuUuid = key,
            });
            return;
        }

        lock (Lock)
        {
            try
            {
                string path = PathFor(key);
                var state = ReadFile(path) ?? Empty(ProbeLockPendingName);
                WriteFile(path, state with
                {
                    AppliedAt = DateTimeOffset.Now,
                    CleanShutdown = false,
                    ProbeLockPending = true,
                    GpuUuid = key,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The probe's clock pin on <paramref name="key"/> is released: an
    /// index-fallback record is dropped outright (nothing else lives in it);
    /// a real UUID's file only loses the probe flag — the rest of the record
    /// (a tuning lock, fan control) is still the tuner's to track. The legacy
    /// file is never touched (see <see cref="RecordProbeLockPending"/>).
    /// </summary>
    public static void ResolveProbeLock(string key)
    {
        lock (Lock)
        {
            try
            {
                string path = PathFor(key);
                if (IsIndexKey(key))
                {
                    File.Delete(path);
                }
                else if (ReadFile(path) is { ProbeLockPending: true } state)
                {
                    if (state.ProfileName == ProbeLockPendingName)
                    {
                        // Born from the probe, nothing else in it. Left behind
                        // unclean under the name "v/f probe clock lock", it
                        // raised the generic crash banner after a clean CLI
                        // re-probe — the CLI never marks the session clean.
                        File.Delete(path);
                    }
                    else
                    {
                        WriteFile(path, state with { ProbeLockPending = false });
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsIntelUuid(string? gpuUuid) =>
        gpuUuid is not null && gpuUuid.StartsWith("INTEL-", StringComparison.OrdinalIgnoreCase);

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
                bool ownsLegacy = legacy is null || legacy.GpuUuid == gpuUuid || gpuUuid is null
                    || (legacy.GpuUuid is null && !IsIntelUuid(gpuUuid));
                if (ownsLegacy)
                {
                    File.Delete(AppPaths.AppliedStateFile);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Per-GPU file name derived from the GPU UUID ("GPU-2b6ae74e-…" or
    /// "INTEL-0000:00:02.0-…" → stable suffix). Vendor prefixes are stripped so
    /// the 12-character budget is spent on the identifying digits.
    /// </summary>
    public static string PathFor(string gpuUuid)
    {
        var keep = new string(gpuUuid.Where(char.IsLetterOrDigit).ToArray());
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
                // leaving a stale copy would double-report crashes. An
                // unstamped legacy record is never an Intel identity's to
                // supersede (it predates Arc write support).
                if (gpuUuid is not null &&
                    ReadFile(AppPaths.AppliedStateFile) is { } legacy &&
                    ((legacy.GpuUuid is null && !IsIntelUuid(gpuUuid)) || legacy.GpuUuid == gpuUuid))
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
