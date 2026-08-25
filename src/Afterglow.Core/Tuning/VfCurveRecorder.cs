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

    public long TotalSamples { get; private set; }

    /// <summary>Feeds one telemetry snapshot into the curve.</summary>
    public void Add(GpuSnapshot snapshot)
    {
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

    /// <summary>The measured curve, voltage-ascending.</summary>
    public IReadOnlyList<VfBin> GetCurve()
    {
        lock (_lock)
        {
            return _bins
                .Where(kv => kv.Value.Samples >= 2)
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
    public double? ClockAt(double voltageMv)
    {
        var curve = GetCurve();
        if (curve.Count == 0)
        {
            return null;
        }

        if (voltageMv <= curve[0].VoltageMv)
        {
            return voltageMv >= curve[0].VoltageMv - (BinMv * 3) ? curve[0].MaxClockMHz : null;
        }

        for (int i = 1; i < curve.Count; i++)
        {
            if (voltageMv <= curve[i].VoltageMv)
            {
                var a = curve[i - 1];
                var b = curve[i];
                double t = (voltageMv - a.VoltageMv) / (b.VoltageMv - a.VoltageMv);
                return a.MaxClockMHz + (t * (b.MaxClockMHz - a.MaxClockMHz));
            }
        }

        return voltageMv <= curve[^1].VoltageMv + (BinMv * 3) ? curve[^1].MaxClockMHz : null;
    }

    /// <summary>
    /// Computes the tuning needed to hold <paramref name="targetClockMHz"/> at
    /// <paramref name="targetVoltageMv"/>: a core offset that lifts the curve by the
    /// difference, plus a clock lock so the GPU never boosts past the target (which
    /// would require more voltage). Returns null when the curve lacks coverage.
    /// </summary>
    public UndervoltPlan? PlanUndervolt(double targetVoltageMv, double targetClockMHz, int currentOffsetMHz)
    {
        if (ClockAt(targetVoltageMv) is not double measuredClock)
        {
            return null;
        }

        // The measured curve already includes the current offset, so the delta is
        // added on top of it.
        int requiredOffset = (int)Math.Round(currentOffsetMHz + (targetClockMHz - measuredClock));
        return new UndervoltPlan(
            TargetVoltageMv: targetVoltageMv,
            TargetClockMHz: targetClockMHz,
            MeasuredClockAtVoltage: measuredClock,
            CoreOffsetMHz: requiredOffset,
            LockClockMHz: (uint)Math.Round(targetClockMHz));
    }

    // --- Persistence ---------------------------------------------------------

    private sealed record PersistedBin(int Key, double MaxClock, double ClockSum, long Samples);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string DefaultPath => Path.Combine(AppPaths.Root, "vf-curve.json");

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

            File.WriteAllText(path ?? DefaultPath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Load(string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            if (!File.Exists(file))
            {
                return;
            }

            var data = JsonSerializer.Deserialize<PersistedBin[]>(File.ReadAllText(file), JsonOptions);
            if (data is null)
            {
                return;
            }

            lock (_lock)
            {
                _bins.Clear();
                TotalSamples = 0;
                foreach (var entry in data)
                {
                    _bins[entry.Key] = new Bin
                    {
                        MaxClock = entry.MaxClock,
                        ClockSum = entry.ClockSum,
                        Samples = entry.Samples,
                    };
                    TotalSamples += entry.Samples;
                }
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
    uint LockClockMHz)
{
    public string Describe() =>
        $"Hold {TargetClockMHz:F0} MHz at ~{TargetVoltageMv:F0} mV: " +
        $"core offset {(CoreOffsetMHz >= 0 ? "+" : string.Empty)}{CoreOffsetMHz} MHz with the clock locked at {LockClockMHz} MHz " +
        $"(measured {MeasuredClockAtVoltage:F0} MHz at that voltage today).";
}
