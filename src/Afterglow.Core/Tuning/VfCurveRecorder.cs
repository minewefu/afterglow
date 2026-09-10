using System.Text.Json;
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
    private readonly object _lock = new();

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


    /// <summary>Feeds one telemetry snapshot into the curve.</summary>
    public void Add(GpuSnapshot snapshot)
    {
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

            return;
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

            return;
        }

        lock (_lock)
        {
            SamplesWithVoltage++;
        }


        if (load < MinLoadPct || mv < MinVoltageMv || mv > MaxVoltageMv || mhz < 200)
        {
            return;
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
    /// Computes the tuning needed to hold <paramref name="targetClockMHz"/> at
    /// <paramref name="targetVoltageMv"/>: a core offset that lifts the curve by the
    /// difference, plus a clock lock so the GPU never boosts past the target (which
    /// would require more voltage). Returns null when the curve lacks coverage.
    /// </summary>
    public UndervoltPlan? PlanUndervolt(
        double targetVoltageMv, double targetClockMHz, int currentOffsetMHz, TuningCapabilities? caps = null)
    {
        // Plans drive hardware writes, so they require well-populated bins
        // (PlanMinBinSamples), unlike merely drawing the curve.
        if (ClockPointAt(targetVoltageMv, PlanMinBinSamples) is not var (measuredClock, binSamples))
        {
            return null;
        }

        // The measured curve already includes the current offset, so the delta is
        // added on top of it.
        int requiredOffset = (int)Math.Round(currentOffsetMHz + (targetClockMHz - measuredClock));
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

    private sealed record PersistedBin(int Key, double MaxClock, double ClockSum, long Samples);

    /// <summary>
    /// Envelope written since the identity stamp was added. The bare
    /// <see cref="PersistedBin"/> array is still read (that is what earlier
    /// versions wrote) under the legacy rule in <see cref="Load"/>.
    /// </summary>
    private sealed record PersistedCurve(string? GpuUuid, uint? VendorId, PersistedBin[] Bins);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string DefaultPath => Path.Combine(AppPaths.Root, "vf-curve.json");

    /// <summary>
    /// Per-GPU curve file. The primary GPU keeps the legacy vf-curve.json (its
    /// data predates multi-GPU and stays valid); other cards get their own file
    /// so one card's V/F points never plan an undervolt for another.
    /// </summary>
    public static string PathFor(string? gpuUuid, bool isPrimary)
    {
        if (isPrimary || string.IsNullOrEmpty(gpuUuid))
        {
            return DefaultPath;
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

        string suffix = keep.Length > 0 ? keep[..Math.Min(12, keep.Length)].ToLowerInvariant() : "unknown";
        return Path.Combine(AppPaths.Root, $"vf-curve-{suffix}.json");
    }

    /// <summary>File this recorder loads from and saves to (null = <see cref="DefaultPath"/>).</summary>
    public string? PersistPath { get; set; }

    /// <summary>
    /// Persists the curve. Returns false when the file was NOT written — because
    /// it belongs to another GPU (see below) or could not be opened — so a caller
    /// can say so rather than let the user believe their measurement was kept.
    /// </summary>
    public bool Save(string? path = null)
    {
        try
        {

            AppPaths.EnsureCreated();
            PersistedBin[] data;
            lock (_lock)
            {
                data = _bins.Select(kv => new PersistedBin(kv.Key, kv.Value.MaxClock, kv.Value.ClockSum, kv.Value.Samples)).ToArray();
            }

            string target = path ?? PersistPath ?? DefaultPath;

            // Guard the WRITE as well as the read. The read half refuses a file
            // stamped for another card, but nothing stopped this card from
            // overwriting it — and the primary GPU's legacy vf-curve.json is a
            // shared, positional name, so a second card that became GPU 0 (or a
            // `--fresh` run, which skips Load entirely) would replace another
            // card's measured curve with its own.
            if (File.Exists(target) && ReadIdentity(target) is { } stamp
                && !IdentityMatches(stamp.Uuid, stamp.VendorId))
            {
                Diagnostics.Log.Info(
                    $"Not overwriting the V/F curve at {target}: it belongs to another GPU.");
                return false;
            }

            File.WriteAllText(target, JsonSerializer.Serialize(new PersistedCurve(GpuUuid, VendorId, data), JsonOptions));
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
            string file = path ?? PersistPath ?? DefaultPath;
            if (!File.Exists(file))
            {
                return;
            }

            string text = File.ReadAllText(file);
            PersistedBin[]? data;

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
