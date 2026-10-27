using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Tuning;

/// <summary>One voltage bin of the measured voltage/frequency curve.</summary>
public sealed record VfBin(double VoltageMv, double MaxClockMHz, double AvgClockMHz, long Samples);

/// <summary>
/// Builds the GPU's voltage/frequency curve by measuring it.
///
/// NVIDIA's private curve interfaces are unavailable on RTX 50 (and curve writes
/// are rejected driver-side), so rather than showing nothing, Afterglow records
/// the real operating curve from telemetry: every (core voltage, core clock)
/// sample is binned by voltage, keeping the highest clock seen at that voltage
/// plus a hit count. The result is the curve the GPU actually runs — including
/// the effect of any applied offset, power limit, or thermal throttling, which a
/// static curve read cannot show.
///
/// The curve then drives precise undervolting: to hold clock F at voltage V, the
/// required core offset is F − (measured clock at V), applied together with a
/// clock lock at F.
/// </summary>
public sealed class VfCurveRecorder
{
    /// <summary>Voltage bin width in mV.</summary>
    public const double BinMv = 5;

    private const double MinVoltageMv = 600;
    private const double MaxVoltageMv = 1300;

    private sealed class Bin
    {
        public double MaxClock;
        public double ClockSum;
        public long Samples;
    }

    private readonly SortedDictionary<int, Bin> _bins = [];
    private readonly Lock _lock = new();

    /// <summary>Ignore samples below this GPU load — idle points aren't curve points.</summary>
    public uint MinLoadPct { get; set; } = 25;

    /// <summary>
    /// The card this curve belongs to. When set, samples carrying any other
    /// device index are refused: this curve is turned into real hardware writes,
    /// so one card's V/F points must never be binned into another card's curve.
    /// Null leaves the recorder unbound (demo mode, tests).
    /// </summary>
    public uint? DeviceIndex { get; init; }

    /// <summary>
    /// Identity of the card this curve belongs to, stamped into the file and
    /// checked on load.
    /// <para>
    /// Without it a plain card swap — or any session where NVML fails to
    /// initialise and an Intel iGPU becomes GPU 0 — made the new card inherit
    /// the old card's <c>vf-curve.json</c>. Nothing caught it: the bounds check
    /// on load only rejects impossible numbers, and 700-1100 mV at 2000-3000 MHz
    /// from an NVIDIA card is perfectly plausible. The foreign curve then
    /// rendered as this card's measurement, and on NVIDIA <c>PlanUndervolt</c>
    /// turned it into a real offset-and-lock write — in the direction that
    /// raises clocks. <c>Add()</c> already refuses foreign LIVE samples; this
    /// closes the same hole on the persisted path, following the rule
    /// <c>AppliedStateStore.Load</c> uses.
    /// </para>
    /// </summary>
    public string? GpuUuid { get; init; }

    /// <summary>PCI vendor id of the card this curve belongs to.</summary>
    public uint? VendorId { get; init; }

    /// <summary>Samples refused because they came from another card (expected: 0).</summary>
    public long ForeignSamplesIgnored { get; private set; }

    public long TotalSamples { get; private set; }

    /// <summary>
    /// Samples that arrived with a clock and a load reading but NO core voltage.
    /// A V/F curve is a map of voltage against clock, so on a GPU whose driver
    /// does not report core voltage it can never be drawn — every sample is
    /// dropped here. Counting them is what lets the surfaces say that instead of
    /// showing an empty chart and "collecting…" forever, which is what the
    /// verified Intel Arc B390 (coreVoltageMv: null on every read) produced.
    /// It is a measurement, not an assumption: the count only rises for samples
    /// actually taken.
    /// </summary>
    public long SamplesMissingVoltage { get; private set; }

    /// <summary>Samples that DID carry a core voltage, counted before the load
    /// and range filters so the verdict below cannot be skewed by them.</summary>
    public long SamplesWithVoltage { get; private set; }

    /// <summary>
    /// True once enough samples have been taken to say the voltage sensor is
    /// absent rather than momentarily unread — no sample has ever carried a
    /// voltage, and several have been taken. A single null read is a glitch;
    /// a dozen in a row is a missing sensor.
    /// </summary>
    public bool VoltageSensorLooksAbsent => SamplesWithVoltage == 0 && SamplesMissingVoltage >= 8;

    // --- Tuning epochs (R-20) -------------------------------------------------
    //
    // Every bin is a max clock at a voltage, and that clock includes whatever
    // tuning was in force when it was sampled. Bins used to persist across
    // tuning changes and sessions while PlanUndervolt added the CURRENT core
    // offset to the gap against them: measure 2800 MHz at 900 mV at +0, plan
    // 2900 (+100), apply it, plan the same target again before new samples and
    // the answer was +200. Historical boosted samples biased plans after a
    // reset the same way, because MaxClock keeps the highest clock ever seen.
    //
    // Normalising each sample by the offset it was taken under would keep the
    // data, but it assumes the offset shifts the whole curve uniformly — false
    // for the per-point edits this same page writes. So the recorder keeps ONE
    // epoch: the bins belong to the tuning they were measured under, a change
    // of that tuning discards them, and a plan is only made against the exact
    // tuning its bins carry. A clock lock is not part of the tuning here: a
    // lock limits which points the GPU visits, it does not move the clock at a
    // voltage — and the probe measures the curve BY locking.

    /// <summary>
    /// The tuning every bin was measured under; null when that is unknown (a
    /// curve file written before epochs, or samples taken before anyone said).
    /// A curve of unknown provenance is drawn but never planned from.
    /// </summary>
    public VfTuningState? TuningState
    {
        get
        {
            lock (_lock)
            {
                return _tuningState;
            }
        }
    }

    private VfTuningState? _tuningState;

    /// <summary>
    /// Declares the tuning the card runs NOW. When it differs from the tuning
    /// the bins were measured under — or that is unknown — the bins are
    /// discarded and a new measurement epoch starts under <paramref name="state"/>.
    /// Call it after every tuning write (<see cref="IGpuTuner.StateWritten"/>,
    /// from a thread that may read the driver — not inside the handler) and
    /// before planning. Returns true when measurements were discarded.
    /// </summary>
    public bool SetTuningState(VfTuningState state)
    {
        VfTuningState? previous;
        long discarded;
        lock (_lock)
        {
            if (_tuningState == state)
            {
                return false;
            }

            previous = _tuningState;
            discarded = TotalSamples;
            _tuningState = state;
            if (_bins.Count == 0)
            {
                return false;
            }

            _bins.Clear();
            TotalSamples = 0;
        }

        Diagnostics.Log.Info(
            $"V/F curve{(DeviceIndex is uint own ? $" for GPU {own}" : string.Empty)}: {discarded} sample(s) measured under " +
            $"{previous?.Describe() ?? "unrecorded tuning"} were set aside — the card now runs {state.Describe()}, " +
            "and a plan may only use samples taken under the tuning it builds on.");
        return true;
    }

    /// <summary>
    /// Declares the tuning the bins ALREADY held were measured under, keeping
    /// them. Only for data whose provenance is known by construction — the
    /// demo's synthetic stock curve. Ignored once a tuning has been declared;
    /// live samples of unknown provenance go through
    /// <see cref="SetTuningState"/>, which discards them.
    /// </summary>
    public void AdoptTuningState(VfTuningState state)
    {
        lock (_lock)
        {
            _tuningState ??= state;
        }
    }


    /// <summary>Feeds one telemetry snapshot into the curve.</summary>
    public void Add(GpuSnapshot snapshot) => TryAdd(snapshot, out _);

    /// <summary>
    /// Feeds one telemetry snapshot into the curve and says whether it was
    /// binned. Returns true with the voltage of the bin it went into; false
    /// when the sample was refused (another card, no voltage, idle load, out of
    /// range). The probe uses the bin to know when a step has collected enough
    /// samples for <see cref="PlanUndervolt(double, double, VfTuningState, TuningCapabilities?)"/> (R-47).
    /// </summary>
    public bool TryAdd(GpuSnapshot snapshot, out double binVoltageMv)
    {
        binVoltageMv = 0;

        // Checked, not trusted: the probe samples through a caller-supplied
        // delegate, and a delegate pointing at the wrong card would otherwise
        // write another GPU's silicon into this card's persisted curve.
        if (DeviceIndex is uint own && snapshot.DeviceIndex != own)
        {
            bool first;
            lock (_lock)
            {
                first = ++ForeignSamplesIgnored == 1;
            }

            if (first)
            {
                Diagnostics.Log.Warn(
                    $"V/F curve for GPU {own} ignored a sample from GPU {snapshot.DeviceIndex}; " +
                    "the curve stays this card's.");
            }

            return false;
        }

        if (snapshot.CoreVoltageMv is not double mv ||
            snapshot.CoreClockMHz is not uint mhz ||
            snapshot.GpuUtilPct is not uint load)
        {
            // Count the specific case of "the clock and load were read, the
            // voltage was not" — that is a missing voltage sensor, and it is the
            // difference between a curve that has not filled in yet and one that
            // never can.
            if (snapshot.CoreClockMHz is not null)
            {
                lock (_lock)
                {
                    if (snapshot.CoreVoltageMv is null)
                    {
                        SamplesMissingVoltage++;
                    }
                    else
                    {
                        SamplesWithVoltage++;
                    }
                }
            }

            return false;
        }

        lock (_lock)
        {
            SamplesWithVoltage++;
        }

        // Finite first (R-21): the range bounds are false for NaN, which then
        // rounded to bin 0 — a "0 mV" point in a curve that drives writes.
        if (!double.IsFinite(mv) || load < MinLoadPct || mv < MinVoltageMv || mv > MaxVoltageMv || mhz < 200)
        {
            return false;
        }

        int key = (int)Math.Round(mv / BinMv);
        lock (_lock)
        {
            if (!_bins.TryGetValue(key, out var bin))
            {
                bin = new Bin();
                _bins[key] = bin;
            }

            bin.MaxClock = Math.Max(bin.MaxClock, mhz);
            bin.ClockSum += mhz;
            bin.Samples++;
            TotalSamples++;
        }

        binVoltageMv = key * BinMv;
        return true;
    }

    /// <summary>
    /// Samples in the bin that holds <paramref name="voltageMv"/> — what
    /// <see cref="PlanMinBinSamples"/> is compared against. 0 for an empty bin.
    /// </summary>
    public long SamplesInBinAt(double voltageMv)
    {
        if (!double.IsFinite(voltageMv))
        {
            return 0;
        }

        lock (_lock)
        {
            return _bins.TryGetValue((int)Math.Round(voltageMv / BinMv), out var bin) ? bin.Samples : 0;
        }
    }

    /// <summary>
    /// Minimum samples a bin needs before it may drive an undervolt plan.
    /// Drawing the curve tolerates thin bins (>= 2); a hardware write does not:
    /// a single transition-glitched sample (voltage and clock are read by
    /// separate driver calls) can pair a low voltage with a high clock.
    /// </summary>
    public const long PlanMinBinSamples = 20;

    /// <summary>The measured curve, voltage-ascending.</summary>
    public IReadOnlyList<VfBin> GetCurve(long minSamples = 2)
    {
        lock (_lock)
        {
            return _bins
                .Where(kv => kv.Value.Samples >= minSamples)
                .Select(kv => new VfBin(
                    kv.Key * BinMv,
                    kv.Value.MaxClock,
                    kv.Value.ClockSum / kv.Value.Samples,
                    kv.Value.Samples))
                .ToArray();
        }
    }

    /// <summary>Highest sample count in any bin (for hit-density shading).</summary>
    public long PeakBinSamples()
    {
        lock (_lock)
        {
            return _bins.Count == 0 ? 0 : _bins.Values.Max(b => b.Samples);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _bins.Clear();
            TotalSamples = 0;

            // Reset BOTH counters. Clearing only the positive one let "Reset
            // curve" flip VoltageSensorLooksAbsent true on a card that had just
            // produced a curve — printing "this GPU's driver does not report
            // core voltage" and refusing the probe on hardware that plainly does.
            SamplesMissingVoltage = 0;
            SamplesWithVoltage = 0;

            // The tuning stays declared: clearing the curve does not change what
            // the card runs, so new samples still belong to the same epoch (R-20).
        }
    }

    /// <summary>
    /// Interpolates the measured max clock at a voltage. Returns null when the
    /// curve has no coverage near that voltage (never observed there).
    /// </summary>
    public double? ClockAt(double voltageMv, long minSamples = 2) =>
        ClockPointAt(voltageMv, minSamples)?.ClockMHz;

    private (double ClockMHz, long Samples)? ClockPointAt(double voltageMv, long minSamples)
    {
        var curve = GetCurve(minSamples);
        if (curve.Count == 0)
        {
            return null;
        }

        if (voltageMv <= curve[0].VoltageMv)
        {
            return voltageMv >= curve[0].VoltageMv - (BinMv * 3)
                ? (curve[0].MaxClockMHz, curve[0].Samples)
                : null;
        }

        for (int i = 1; i < curve.Count; i++)
        {
            if (voltageMv <= curve[i].VoltageMv)
            {
                var a = curve[i - 1];
                var b = curve[i];
                double t = (voltageMv - a.VoltageMv) / (b.VoltageMv - a.VoltageMv);
                return (a.MaxClockMHz + (t * (b.MaxClockMHz - a.MaxClockMHz)), Math.Min(a.Samples, b.Samples));
            }
        }

        return voltageMv <= curve[^1].VoltageMv + (BinMv * 3)
            ? (curve[^1].MaxClockMHz, curve[^1].Samples)
            : null;
    }

    /// <summary>
    /// <see cref="PlanUndervolt(double, double, VfTuningState, TuningCapabilities?)"/>
    /// for a card whose only curve-shaping tuning is a global core offset
    /// (no per-point edits). The bins must have been measured under exactly
    /// that offset (R-20).
    /// </summary>
    public UndervoltPlan? PlanUndervolt(
        double targetVoltageMv, double targetClockMHz, int currentOffsetMHz, TuningCapabilities? caps = null) =>
        PlanUndervolt(targetVoltageMv, targetClockMHz, new VfTuningState(currentOffsetMHz), caps);

    /// <summary>
    /// Computes the tuning needed to hold <paramref name="targetClockMHz"/> at
    /// <paramref name="targetVoltageMv"/>: a core offset that lifts the curve by the
    /// difference, plus a clock lock so the GPU never boosts past the target (which
    /// would require more voltage). Returns null when the curve lacks coverage,
    /// when its bins were measured under any tuning other than
    /// <paramref name="current"/> (or under unknown tuning), and when
    /// <paramref name="current"/> carries per-point edits.
    /// </summary>
    public UndervoltPlan? PlanUndervolt(
        double targetVoltageMv, double targetClockMHz, VfTuningState current, TuningCapabilities? caps = null)
    {
        // Finite inputs only (R-21). The bounds below happened to reject NaN
        // and infinity today; that rested on how a saturating cast lands, not
        // on a check.
        if (!double.IsFinite(targetVoltageMv) || !double.IsFinite(targetClockMHz))
        {
            return null;
        }

        // The bins must have been measured under exactly the tuning planned
        // against (R-20): the arithmetic below assumes the measured clock
        // already contains current.CoreOffsetMHz and nothing else. Unknown
        // provenance (null) never matches.
        if (TuningState != current)
        {
            return null;
        }

        // A plan is one global core offset, and applying it erases per-point
        // edits. Under a shaped table the measured clock at this voltage
        // contains that point's own offset, not the global one, so
        // "current + (target - measured)" is not the offset that holds the
        // target once the shape is gone.
        if (current.PointShape is not null)
        {
            return null;
        }

        // Plans drive hardware writes, so they require well-populated bins
        // (PlanMinBinSamples), unlike merely drawing the curve.
        if (ClockPointAt(targetVoltageMv, PlanMinBinSamples) is not var (measuredClock, binSamples))
        {
            return null;
        }

        // A new epoch may have started while the curve was read; the bins
        // just used may then not be the ones the check above vouched for.
        if (TuningState != current)
        {
            return null;
        }

        // The measured curve already includes the current offset (the epoch
        // check above is what makes that true), so the delta is added on top.
        int requiredOffset = (int)Math.Round(current.CoreOffsetMHz + (targetClockMHz - measuredClock));
        uint lockClock = (uint)Math.Round(Math.Max(0, targetClockMHz));

        // Refuse plans that cannot be applied instead of describing nonsense
        // in a confident tone. With driver capabilities, validate against the
        // real ranges; without them, against the same schema bounds the
        // profile validator enforces.
        if (caps is { SupportsCoreOffset: true } &&
            (requiredOffset < caps.CoreOffsetMinMHz || requiredOffset > caps.CoreOffsetMaxMHz))
        {
            return null;
        }

        // An undervolt IS a core offset. On a GPU whose driver exposes no
        // core-offset knob the plan can never be honoured, yet it was still
        // produced and described in full confidence ("core offset -180 MHz with
        // the clock locked at 2700 MHz"); the apply path then clamped the offset
        // into the capability struct's 0..0 default, wrote only the clock lock,
        // and reported no failure. Refuse to plan what cannot be applied.
        if (caps is { SupportsCoreOffset: false } && requiredOffset != 0)
        {
            return null;
        }

        if (requiredOffset is < -1500 or > 1500 || lockClock is < 210 or > 4500)
        {
            return null;
        }

        return new UndervoltPlan(
            TargetVoltageMv: targetVoltageMv,
            TargetClockMHz: targetClockMHz,
            MeasuredClockAtVoltage: measuredClock,
            CoreOffsetMHz: requiredOffset,
            LockClockMHz: lockClock,
            BinSamples: binSamples);
    }

    // --- Persistence ---------------------------------------------------------
    //
    // A measured curve is one card's silicon, and PlanUndervolt turns it into a
    // core offset plus a clock lock. So a curve file belongs to a GPU identity
    // (the NVML UUID), never to an enumeration position (F-15, F-55): the file
    // name is derived from the UUID, the content carries the full UUID, and a
    // card with no UUID persists nothing at all rather than share a file.

    private sealed record PersistedBin(int Key, double MaxClock, double ClockSum, long Samples);

    /// <summary>
    /// The on-disk shape: the owning card's identity (UUID and PCI vendor), the
    /// tuning its bins were measured under (R-20; absent in files written
    /// before epochs, which then load as unknown provenance), and its bins. A
    /// bare <see cref="PersistedBin"/> array is still read — that is what
    /// earlier versions wrote — under the legacy rule in <see cref="Load"/>.
    /// </summary>
    private sealed record PersistedCurve(
        string? GpuUuid,
        uint? VendorId,
        PersistedBin[] Bins,
        int? TuningCoreOffsetMHz = null,
        string? TuningPointShape = null);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// The pre-identity shared file, which the primary card used to load
    /// whatever card it was. It carries no GPU stamp, so nothing it holds can be
    /// attributed to a card: it is no longer read or written by any recorder,
    /// and is left on disk untouched.
    /// </summary>
    public static string DefaultPath => Path.Combine(AppPaths.Root, "vf-curve.json");

    /// <summary>
    /// A recorder bound to one card: refuses other cards' samples, and loads
    /// and saves the curve file keyed by that card's UUID. Call
    /// <see cref="Load(string?)"/> to restore what it measured before.
    /// </summary>
    public static VfCurveRecorder ForGpu(uint deviceIndex, string? gpuUuid, uint? vendorId = null) => new()
    {
        DeviceIndex = deviceIndex,
        GpuUuid = gpuUuid,
        VendorId = vendorId,
        PersistPath = PathFor(gpuUuid),
    };

    /// <summary>
    /// The curve file for one GPU identity, or null when there is no identity
    /// to key it by (the driver would not report a UUID). Null means "do not
    /// persist": two unidentified cards would otherwise share one file and
    /// merge each other's silicon into it. Every card — primary or not — gets
    /// its own file; which card NVML happens to enumerate first is not an
    /// identity, and a card swap or a slot change must not hand one card's
    /// measured curve to another.
    /// </summary>
    public static string? PathFor(string? gpuUuid)
    {
        if (string.IsNullOrEmpty(gpuUuid))
        {
            return null;
        }

        var keep = new string(gpuUuid.Where(char.IsLetterOrDigit).ToArray());
        if (keep.StartsWith("INTEL", StringComparison.OrdinalIgnoreCase))
        {
            keep = "i" + keep[5..]; // same strip rule as AppliedStateStore.PathFor
        }
        else if (keep.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
        {
            keep = keep[3..];
        }

        if (keep.Length == 0)
        {
            return null;
        }

        // Same naming as the per-card files earlier versions wrote for
        // secondary cards, so those curves stay with their cards.
        return Path.Combine(AppPaths.Root, $"vf-curve-{keep[..Math.Min(12, keep.Length)].ToLowerInvariant()}.json");
    }

    /// <summary>
    /// Kept for existing callers. <paramref name="isPrimary"/> no longer
    /// selects the shared legacy file: that was keyed by enumeration position,
    /// so a different card in the first slot inherited the previous card's
    /// curve and planned undervolts from it (F-15, F-55).
    /// </summary>
    public static string? PathFor(string? gpuUuid, bool isPrimary)
    {
        _ = isPrimary;
        return PathFor(gpuUuid);
    }

    /// <summary>
    /// File this recorder loads from and saves to. Null = not persisted: a
    /// <see cref="Save"/> or <see cref="Load"/> without an explicit path does
    /// nothing (an unidentified card, demo mode).
    /// </summary>
    public string? PersistPath { get; set; }

    /// <summary>
    /// Persists the curve. Returns false when the file was NOT written — because
    /// it belongs to another GPU (see below) or could not be opened — so a caller
    /// can say so rather than let the user believe their measurement was kept.
    /// </summary>
    public bool Save(string? path = null)
    {
        string? file = path ?? PersistPath;
        if (file is null)
        {
            return false;
        }

        try
        {
            AppPaths.EnsureCreated();
            PersistedBin[] data;
            VfTuningState? tuning;
            lock (_lock)
            {
                // Bins and their epoch in one snapshot: a stamp read separately
                // could label one epoch's bins with the next epoch's tuning.
                data = _bins.Select(kv => new PersistedBin(kv.Key, kv.Value.MaxClock, kv.Value.ClockSum, kv.Value.Samples)).ToArray();
                tuning = _tuningState;
            }

            // Guard the WRITE as well as the read. The read half refuses a file
            // stamped for another card; nothing else would stop this card from
            // overwriting one (a `--fresh` run skips Load entirely).
            if (File.Exists(file) && ReadIdentity(file) is { } stamp
                && !IdentityMatches(stamp.Uuid, stamp.VendorId))
            {
                Diagnostics.Log.Info(
                    $"Not overwriting the V/F curve at {file}: it belongs to another GPU.");
                return false;
            }

            File.WriteAllText(file, JsonSerializer.Serialize(
                new PersistedCurve(GpuUuid, VendorId, data, tuning?.CoreOffsetMHz, tuning?.PointShape), JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log.Warn($"V/F curve could not be saved: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads just the identity stamp from a curve file. Returns null when the
    /// file cannot be read or is the legacy unstamped array — neither is a
    /// mismatch, so both leave the write path alone.
    /// </summary>
    private static (string? Uuid, uint? VendorId)? ReadIdentity(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            if (!text.TrimStart().StartsWith('{'))
            {
                return null;
            }

            var envelope = JsonSerializer.Deserialize<PersistedCurve>(text, JsonOptions);
            return envelope is null ? null : (envelope.GpuUuid, envelope.VendorId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a stamped file was written by the card this recorder serves.
    /// A recorder with no identity of its own (the CLI's transient recorders on
    /// a card that reports no UUID) accepts any stamp — it has nothing to
    /// compare against, and refusing would break the single-GPU case.
    /// </summary>
    private bool IdentityMatches(string? fileUuid, uint? fileVendor)
    {
        if (GpuUuid is { Length: > 0 } mine && fileUuid is { Length: > 0 } theirs)
        {
            return string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase);
        }

        if (VendorId is { } mineVendor && fileVendor is { } theirVendor)
        {
            return mineVendor == theirVendor;
        }

        return true;
    }

    public void Load(string? path = null)
    {
        try
        {
            string? file = path ?? PersistPath;
            if (file is null || !File.Exists(file))
            {
                return;
            }

            string text = File.ReadAllText(file);
            PersistedBin[]? data;
            VfTuningState? tuning = null; // a legacy or pre-epoch file: provenance unknown (R-20)

            // Stamped envelope, or the legacy bare array earlier versions wrote.
            if (text.TrimStart().StartsWith('{'))
            {
                var envelope = JsonSerializer.Deserialize<PersistedCurve>(text, JsonOptions);
                if (envelope is null)
                {
                    return;
                }

                if (!IdentityMatches(envelope.GpuUuid, envelope.VendorId))
                {
                    Diagnostics.Log.Info(
                        $"V/F curve at {file} belongs to another GPU — not loading it for GPU {DeviceIndex}. " +
                        "A curve measured on one card cannot plan an undervolt for another.");
                    return;
                }

                data = envelope.Bins;
                if (envelope.TuningCoreOffsetMHz is int core)
                {
                    tuning = new VfTuningState(core, envelope.TuningPointShape);
                }
            }
            else
            {
                // An unstamped file predates per-GPU identity. It migrates for
                // the card that plausibly wrote it — the historical single-GPU
                // NVIDIA case — and never for an Intel identity, which cannot
                // have written one: this branch only ever ran on NVIDIA.
                if (VendorId is { } vendor && vendor != Stress.StressAdapter.NvidiaVendorId)
                {
                    Diagnostics.Log.Info(
                        $"Ignoring the unstamped legacy V/F curve at {file} for GPU {DeviceIndex}: " +
                        "it predates per-GPU curves and cannot have been measured on this card.");
                    return;
                }

                data = JsonSerializer.Deserialize<PersistedBin[]>(text, JsonOptions);
            }

            if (data is null)
            {
                return;
            }

            // Persisted bins get the same bounds Add() enforces — a corrupted
            // or hand-edited file must not feed arbitrary clocks into
            // PlanUndervolt (which turns them into hardware writes).
            int dropped = 0;
            lock (_lock)
            {
                _bins.Clear();
                TotalSamples = 0;
                _tuningState = tuning; // the bins arrive with the epoch they were measured in
                foreach (var entry in data)
                {
                    double voltage = entry.Key * BinMv;
                    double avg = entry.Samples > 0 ? entry.ClockSum / entry.Samples : 0;
                    if (voltage is < MinVoltageMv or > MaxVoltageMv ||
                        entry.Samples <= 0 ||
                        entry.MaxClock is < 200 or > 5000 ||
                        avg is < 200 or > 5000 ||
                        avg > entry.MaxClock + 1)
                    {
                        dropped++;
                        continue;
                    }

                    _bins[entry.Key] = new Bin
                    {
                        MaxClock = entry.MaxClock,
                        ClockSum = entry.ClockSum,
                        Samples = entry.Samples,
                    };
                    TotalSamples += entry.Samples;
                }
            }

            if (dropped > 0)
            {
                Diagnostics.Log.Info($"V/F curve load: dropped {dropped} out-of-bounds bin(s) from {file}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }
}

/// <summary>A concrete undervolt derived from the measured curve.</summary>
public sealed record UndervoltPlan(
    double TargetVoltageMv,
    double TargetClockMHz,
    double MeasuredClockAtVoltage,
    int CoreOffsetMHz,
    uint LockClockMHz,
    long BinSamples)
{
    public string Describe() =>
        $"Hold {TargetClockMHz:F0} MHz at ~{TargetVoltageMv:F0} mV: " +
        $"core offset {(CoreOffsetMHz >= 0 ? "+" : string.Empty)}{CoreOffsetMHz} MHz with the clock locked at {LockClockMHz} MHz " +
        $"(measured {MeasuredClockAtVoltage:F0} MHz at that voltage today, {BinSamples} samples in that bin).";
}

/// <summary>
/// The tuning a measured V/F curve is only valid under (R-20): what moves the
/// clock the GPU runs at a given voltage. That is the global core offset and
/// any per-point edits to the stored table — not a clock lock (it limits which
/// points are visited, and the probe measures by locking), not the power limit
/// (it moves the operating point along the curve, not the curve).
/// </summary>
/// <param name="CoreOffsetMHz">The global core offset in force.</param>
/// <param name="PointShape">
/// Null when the stored table carries no per-point edits — every slot reads
/// the core offset, or the GPU exposes no per-point table at all. Otherwise a
/// fingerprint of the slot offsets, or <see cref="UnreadableShape"/> when the
/// table exists but did not read back (edits cannot then be ruled out).
/// </param>
public readonly record struct VfTuningState(int CoreOffsetMHz, string? PointShape = null)
{
    /// <summary>The shape recorded when the table exists but would not read back.</summary>
    public const string UnreadableShape = "unreadable";

    /// <summary>No core offset, no per-point edits.</summary>
    public static VfTuningState Stock => default;

    /// <summary>
    /// Reads the tuning a card runs now. Driver reads only — no write, no
    /// notification — but not free either: call it from a UI or worker thread,
    /// never from inside a <see cref="IGpuTuner.StateWritten"/> handler.
    /// </summary>
    public static VfTuningState Read(IGpuTuner tuner)
    {
        int core = tuner.ReadCurrent().CoreOffsetMHz;
        if (!tuner.Capabilities.SupportsVfPoints)
        {
            return new VfTuningState(core);
        }

        return tuner.TryReadVfPoints(out var points) == NvapiStatus.Ok
            ? new VfTuningState(core, ShapeOf(points, core))
            : new VfTuningState(core, UnreadableShape);
    }

    /// <summary>
    /// Null when every slot reads the global core offset (the table holds
    /// nothing the offset alone does not); otherwise a stable fingerprint of
    /// the slot offsets. Only offsets are hashed: the stored base clocks can
    /// move with temperature without any tuning having changed.
    /// </summary>
    public static string? ShapeOf(IReadOnlyList<NvapiGpu.VfpTablePoint> points, int coreOffsetMHz)
    {
        if (points.All(point => point.OffsetMHz == coreOffsetMHz))
        {
            return null;
        }

        string canonical = string.Join(',', points
            .OrderBy(point => point.Index)
            .Select(point => FormattableString.Invariant($"{point.Index}:{point.OffsetMHz}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    /// <summary>For logs and status lines: "core offset +100 MHz" (and whether the table is shaped).</summary>
    public string Describe() =>
        FormattableString.Invariant($"core offset {CoreOffsetMHz:+0;-0;0} MHz") +
        (PointShape switch
        {
            null => string.Empty,
            UnreadableShape => " (the stored V/F table did not read back)",
            _ => " with per-point V/F edits",
        });
}
