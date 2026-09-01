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

    /// <summary>Samples refused because they came from another card (expected: 0).</summary>
    public long ForeignSamplesIgnored { get; private set; }

    public long TotalSamples { get; private set; }

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
            return;
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

    public void Save(string? path = null)
    {
        try
        {
            AppPaths.EnsureCreated();
            PersistedBin[] data;
            lock (_lock)
            {
                data = _bins.Select(kv => new PersistedBin(kv.Key, kv.Value.MaxClock, kv.Value.ClockSum, kv.Value.Samples)).ToArray();
            }

            File.WriteAllText(path ?? PersistPath ?? DefaultPath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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

            var data = JsonSerializer.Deserialize<PersistedBin[]>(File.ReadAllText(file), JsonOptions);
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
