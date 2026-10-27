using System.Globalization;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;

namespace Afterglow.Core.Tuning;

/// <summary>
/// What a V/F sweep starts from (see <see cref="IGpuTuner.BeginProbe"/>).
/// </summary>
/// <param name="MaxClockMHz">The highest clock the sweep may pin: the domain maximum, or the released ceiling once one has been verified.</param>
/// <param name="LockToRestoreMHz">The range lock this process had applied when the sweep began. <see cref="IGpuTuner.EndProbe"/> puts back the lock in force at its end, which is this one unless a newer lock was written (or the lock released) during the sweep.</param>
/// <param name="ForeignClampNote">Set when a clamp this process had not applied was released before the sweep, for the outcome text.</param>
/// <param name="Refusal">Non-null when the card could not be prepared (a clamp that would not release); nothing was pinned.</param>
public sealed record ProbeStart(uint MaxClockMHz, uint? LockToRestoreMHz, string? ForeignClampNote, string? Refusal = null);

/// <summary>What a tuning write changes, for the flight recording and the crash verdict that reads it.</summary>
public enum TuningWriteKind
{
    /// <summary>A profile apply: offsets, power, voltage, temperature limit, clock lock and V/F points together.</summary>
    Apply,

    /// <summary>A return to driver defaults.</summary>
    Reset,

    /// <summary>A direct per-point V/F write or clear.</summary>
    VfPoints,

    /// <summary>A clock-lock write or release outside a profile apply.</summary>
    Lock,

    /// <summary>The V/F probe's exact pin.</summary>
    ProbePin,
}

/// <summary>
/// One write about to reach the driver (see <see cref="IGpuTuner.WriteStarting"/>).
/// <paramref name="What"/> is a present-participle phrase — "applying profile
/// 'Gaming' (core +150 MHz)" — so a report can say "while Afterglow was …".
/// <paramref name="Id"/> pairs the start with its finish; it is unique in the process.
/// </summary>
public sealed record TuningWrite(long Id, TuningWriteKind Kind, string What)
{
    /// <summary>"applying profile 'Gaming' (core +150 MHz, lock 1500 MHz)": the knobs the profile sets, with their values.</summary>
    public static string DescribeApply(TuningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var knobs = new List<string>(6);
        if (profile.CoreOffsetMHz != 0)
        {
            knobs.Add($"core {Signed(profile.CoreOffsetMHz)} MHz");
        }

        if (profile.MemOffsetMHz != 0)
        {
            knobs.Add($"memory {Signed(profile.MemOffsetMHz)} MHz");
        }

        if (profile.PowerLimitW is double watts)
        {
            knobs.Add(string.Create(CultureInfo.InvariantCulture, $"power {watts:F0} W"));
        }

        if (profile.VoltageBoostPct is uint boost)
        {
            knobs.Add(string.Create(CultureInfo.InvariantCulture, $"voltage boost {boost}%"));
        }

        if (profile.TempLimitC is uint tempC)
        {
            knobs.Add(string.Create(CultureInfo.InvariantCulture, $"temperature limit {tempC} °C"));
        }

        knobs.Add(profile.LockedCoreClockMHz is uint lockMHz
            ? string.Create(CultureInfo.InvariantCulture, $"lock {lockMHz} MHz")
            : "no clock lock");

        if (profile.VfPointOffsetsMHz is { Count: > 0 } points)
        {
            knobs.Add(DescribePoints(points));
        }

        return $"applying profile '{profile.Name}' ({string.Join(", ", knobs)})";
    }

    /// <summary>"writing per-point V/F offsets (3 points, highest +150 MHz)".</summary>
    public static string DescribeVfPoints(IReadOnlyDictionary<int, int> offsetsMHzByIndex)
    {
        ArgumentNullException.ThrowIfNull(offsetsMHzByIndex);
        return $"writing per-point V/F offsets ({DescribePoints(offsetsMHzByIndex)})";
    }

    private static string DescribePoints(IReadOnlyDictionary<int, int> points) =>
        points.Count == 0
            ? "no points"
            : string.Create(CultureInfo.InvariantCulture,
                $"{points.Count} V/F point{(points.Count == 1 ? string.Empty : "s")}, highest {Signed(points.Values.Max())} MHz");

    private static string Signed(int mhz) => mhz.ToString("+0;-0;0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Brackets one driver write with <see cref="IGpuTuner.WriteStarting"/> and
/// <see cref="IGpuTuner.WriteFinished"/>. The finish is raised from
/// <see cref="Dispose"/>, so a write that throws still finishes, and a write
/// nobody listens to costs neither an allocation nor an id.
/// </summary>
public readonly struct TuningWriteScope : IDisposable
{
    private static long _nextId;

    private readonly IGpuTuner? _tuner;
    private readonly Action<IGpuTuner, TuningWrite>? _finished;
    private readonly TuningWrite? _write;

    private TuningWriteScope(IGpuTuner tuner, Action<IGpuTuner, TuningWrite>? finished, TuningWrite write)
    {
        _tuner = tuner;
        _finished = finished;
        _write = write;
    }

    /// <summary>
    /// Raises <paramref name="starting"/> for a write of <paramref name="kind"/>,
    /// synchronously, before the caller's driver call; the returned scope raises
    /// <paramref name="finished"/> when disposed. Handler failures never reach the caller.
    /// </summary>
    public static TuningWriteScope Begin(
        IGpuTuner tuner,
        Action<IGpuTuner, TuningWrite>? starting,
        Action<IGpuTuner, TuningWrite>? finished,
        TuningWriteKind kind,
        string what)
    {
        ArgumentNullException.ThrowIfNull(tuner);
        if (starting is null && finished is null)
        {
            return default;
        }

        var write = new TuningWrite(Interlocked.Increment(ref _nextId), kind, what);
        WriteNotification.Raise(starting, tuner, write);
        return new TuningWriteScope(tuner, finished, write);
    }

    public void Dispose()
    {
        if (_write is not null)
        {
            WriteNotification.Raise(_finished, _tuner!, _write);
        }
    }
}

/// <summary>
/// The tuning surface every vendor implements. Extracted verbatim from
/// <see cref="GpuTuner"/> so the NVIDIA implementation keeps its exact
/// signatures and semantics (they cannot be regression-tested on non-NVIDIA
/// hardware); consumers gate every knob on <see cref="Capabilities"/>, which
/// each implementation fills by probing its driver live — never by generation
/// or marketing name.
///
/// The lock/fan members return <see cref="NvmlReturn"/> and the V/F members
/// use NVAPI types because those are the signatures the entire app was built
/// against; non-NVIDIA tuners answer them with the honest
/// <see cref="NvmlReturn.NotSupported"/> / <see cref="NvapiStatus.NotSupported"/>
/// values, and the UI never reaches them when the corresponding capability
/// reads false.
/// </summary>
public interface IGpuTuner
{
    TuningCapabilities Capabilities { get; }

    /// <summary>Stable identity profiles and applied state are stamped with.</summary>
    string? GpuUuid { get; }

    /// <summary>
    /// The clock lock Afterglow APPLIED — never a ceiling merely observed from
    /// the driver (NVML has no getter; IGCL does, see <see cref="LockIsDriverReadable"/>,
    /// and there an observed factory ceiling reads as null here), and never the
    /// V/F probe's exact pin: while a sweep runs, and after a pin the sweep
    /// could not release, this is still the range lock the pin displaced.
    /// </summary>
    uint? AppliedLockMHz { get; }

    /// <summary>
    /// True when <c>ReadCurrent().LockedCoreClockMHz</c> comes from a real driver
    /// readback rather than an in-process shadow. Only a driver-backed value can
    /// witness a change made from outside this process — a reset by an
    /// automation rule, a TDR recovery, or another tool — so callers that need
    /// to detect such a change must not rely on the lock unless this is true.
    /// False on NVML (no locked-clock getter exists); true on IGCL.
    /// </summary>
    bool LockIsDriverReadable => false;

    /// <summary>
    /// The key this card's applied-state record is filed under: its UUID, or
    /// the index fallback when the driver reports none. Every writer for the
    /// card — the tuner, its fan service, a probe pinning it — uses this one
    /// key, so the record has one home and a verified lock release from any
    /// process resolves the pin a probe recorded.
    /// </summary>
    string RecordKey => GpuUuid ?? throw new InvalidOperationException("this tuner has no record key");

    /// <summary>
    /// The currently applied values. PowerLimitW is null when the device has
    /// no readable power limit (the NVIDIA tuner always reads one back).
    /// LockedCoreClockMHz is an OBSERVATION — on a driver with a readback
    /// getter it is whatever ceiling the driver reports, factory or foreign
    /// clamp included — and must never be fed back into a profile as the lock
    /// to preserve; that is <see cref="AppliedLockMHz"/>. Carrying the observed
    /// value forward wrote an Arc factory ceiling back as a clamp with written
    /// provenance, which no release could then adopt.
    /// </summary>
    (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz) ReadCurrent();

    /// <summary>
    /// Applies a profile. With <paramref name="releaseLock"/> the clock lock is
    /// released whether or not this session tracks one — an explicit
    /// <c>--lock-clock off</c> or MCP <c>unlock</c> is one operation with one
    /// verdict, reported as the "clock lock" knob. A profile that carries a
    /// lock AND asks for the release is refused as a "profile" failure before
    /// anything is written: two answers to one question are not resolved
    /// silently by any front-end.
    /// </summary>
    ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true, bool releaseLock = false);

    ApplyResult ResetToDefaults();

    KnobResult ForceUnlock();

    /// <summary>Range lock (idle downclock still allowed) — the form profiles apply.</summary>
    NvmlReturn RestoreTuningLock(uint lockMHz);

    /// <summary>Exact pin, required for V/F probing; only meaningful between <see cref="BeginProbe"/> and <see cref="EndProbe"/>.</summary>
    NvmlReturn LockClockForProbe(uint clockMHz);

    /// <summary>
    /// Prepares a V/F sweep: remembers the range lock this process applied (so
    /// <see cref="AppliedLockMHz"/> keeps answering with it while pins stand),
    /// releases whatever clamp is on the card so the sweep runs against the
    /// true ceiling, and reports the ceiling to sweep up to. The tuner owns
    /// this because only it can capture the displaced lock BEFORE the release
    /// empties its tracking — the probe capturing it from outside lost it.
    /// </summary>
    ProbeStart BeginProbe() => new(Capabilities.MaxCoreClockMHz, AppliedLockMHz, null);

    /// <summary>
    /// Ends a V/F sweep: puts back the lock in force — the one
    /// <see cref="BeginProbe"/> remembered, or a newer one written during the
    /// sweep — or releases the pin when there is none (and does nothing when no
    /// pin ever landed). A failed verdict is the restore failure the probe reports.
    /// </summary>
    KnobResult EndProbe() => ForceUnlock();

    NvmlReturn SetAllFansRaw(uint dutyPct);

    /// <summary>
    /// An all-fans write that says what a refusal left on the card
    /// (<see cref="FanWriteResult.FansMayBeManual"/>). A tuner that can't tell per
    /// fan answers as <see cref="FanWriteResult.FromRaw"/> reads one code; the
    /// NVIDIA tuner rolls a partial write back, or puts it on record (R-06).
    /// </summary>
    FanWriteResult SetAllFans(uint dutyPct) => FanWriteResult.FromRaw(SetAllFansRaw(dutyPct));

    NvmlReturn SetFanRaw(uint coolerId, uint dutyPct);

    NvmlReturn RestoreAutoFansRaw();

    NvapiStatus TryReadVfPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points);

    KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex);

    KnobResult ClearVfPointOffsets();

    /// <summary>
    /// The temperature limit currently in force, in °C — null when the card has
    /// no temperature-limit control or the driver would not report it. Read-only:
    /// a game rule reads it before its profile lands so the exit restore can put
    /// back the value it found (F-21).
    /// </summary>
    int? ReadTempLimitC() => null;

    /// <summary>
    /// The lowest clock lock and power limit a profile may carry on this device
    /// (<see cref="TuningProfile.Validate"/>'s floors): NVML's 210 MHz and 50 W by
    /// default; a device whose driver reports lower floors overrides them.
    /// </summary>
    (uint LockFloorMHz, double PowerFloorW) ProfileFloors => (210, 50);

    /// <summary>
    /// Raised after every call that may have changed what this card runs, whoever
    /// made it (a page, a hotkey, the TDR watchdog, the stepper, the certifier,
    /// the V/F probe), so pages that show applied state re-read (F-08, F-75).
    /// Raised on the writing thread after the tuner's lock is released; handlers
    /// must not block, and an exception in one never reaches the write path.
    /// </summary>
    event Action<IGpuTuner>? StateWritten;

    /// <summary>
    /// Raised synchronously on the writing thread, under the tuner's lock, just
    /// before a write that can change what the card runs reaches the driver:
    /// an apply, a reset, a per-point V/F write or clear, a clock-lock write or
    /// release, a probe pin. Fan writes are not tuning writes and raise nothing.
    /// <para>
    /// <see cref="StateWritten"/> fires only after the write returns, so a crash
    /// or TDR the write itself causes left no trace of it: the flight recording
    /// still stated the tuning before the write, and the report blamed — or
    /// cleared — that. A handler that must survive such a crash (the flight
    /// recorder) records the write here, durably, before returning; the write
    /// waits for it. Every start is paired with <see cref="WriteFinished"/>.
    /// </para>
    /// <para>
    /// The default implementation raises nothing: a tuner that does not
    /// announce its writes leaves the recording as it was before.
    /// </para>
    /// </summary>
    event Action<IGpuTuner, TuningWrite>? WriteStarting
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when the driver call announced by <see cref="WriteStarting"/>
    /// returned — whether or not the write succeeded, and also when it threw —
    /// on the writing thread, before <see cref="StateWritten"/>.
    /// </summary>
    event Action<IGpuTuner, TuningWrite>? WriteFinished
    {
        add { }
        remove { }
    }

    /// <summary>
    /// A clock lock this card's record says Afterglow applied, in this boot,
    /// before the system last slept or hibernated (Fast Startup included): the
    /// driver may or may not have kept it, and NVML cannot be asked. It is not
    /// <see cref="AppliedLockMHz"/> — nothing re-applies it or carries it
    /// forward — and the front-ends say it may still be in force. Cleared by
    /// any lock write or verified release. Null on tuners whose lock is driver-readable.
    /// </summary>
    uint? UnconfirmedLockMHz => null;
}
