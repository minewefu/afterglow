using Afterglow.Core.Interop.Igcl;

namespace Afterglow.Core.Tests.Fakes;

/// <summary>
/// An in-memory IGCL GPU frequency domain. It models the one thing every
/// clock-lock scenario turns on — what a range write and a factory restore do
/// to the range the driver reads back — with switches for the driver
/// behaviours the tuner defends against. Every switch is a HYPOTHESIS about
/// the real driver; the test that flips it names the scenario it encodes, and
/// a switch no test needs should be deleted along with the branch it exercises.
/// </summary>
internal sealed class FakeArcDevice : IArcDevice
{
    public const nint GpuDomain = 0x1000;

    /// <summary>The frequency domain's own limits (ctl_freq_properties_t min/max).</summary>
    public double HwMin { get; init; } = 100;

    public double HwMax { get; init; } = 2300;

    /// <summary>The floor a factory restore (-1/-1) lands at.</summary>
    public double FactoryMin { get; init; } = 100;

    /// <summary>
    /// The ceiling a factory restore lands at. Below <see cref="HwMax"/> this
    /// models a card whose factory ceiling sits under the domain maximum —
    /// the shape that produced most of the phantom-clamp findings.
    /// </summary>
    public double FactoryMax { get; init; } = 2300;

    private double? _min;
    private double? _max;

    /// <summary>The current range, as the driver would read it back. Starts at the factory range.</summary>
    public double Min
    {
        get => _min ?? FactoryMin;
        set => _min = value;
    }

    public double Max
    {
        get => _max ?? FactoryMax;
        set => _max = value;
    }

    /// <summary>Result of every ordinary range write. A refusal models a non-elevated session.</summary>
    public CtlResult WriteResult { get; set; } = CtlResult.Success;

    /// <summary>Result of factory restores (-1/-1) only.</summary>
    public CtlResult RestoreResult { get; set; } = CtlResult.Success;

    /// <summary>Result of every range read.</summary>
    public CtlResult ReadResult { get; set; } = CtlResult.Success;

    /// <summary>A factory restore drops the floor but leaves the ceiling where it was.</summary>
    public bool KeepCapOnRestore { get; set; }

    /// <summary>A write above the factory ceiling settles at the ceiling instead of being refused.</summary>
    public bool ClampWritesToFactoryMax { get; set; } = true;

    /// <summary>Added to every readback, to model a driver that settles a fraction off the request.</summary>
    public double ReadbackOffset { get; set; }

    /// <summary>Every range write, in order (restores appear as -1/-1).</summary>
    public List<(double Min, double Max)> Writes { get; } = [];

    public int Reads { get; private set; }

    /// <summary>Set when the first exact pin (min == max) is written; lets a test wait for a probe to land one.</summary>
    public ManualResetEventSlim PinLanded { get; } = new();

    /// <summary>The exact pins written so far, in order.</summary>
    public IEnumerable<double> PinnedTargets =>
        Writes.Where(w => w.Min > 0 && w.Min == w.Max).Select(w => w.Max).ToList();

    public IReadOnlyList<(nint Handle, CtlFreqProperties Properties)> GetFrequencyDomains() =>
    [
        (GpuDomain, new CtlFreqProperties
        {
            Type = CtlFreqDomain.Gpu,
            CanControl = 1,
            Min = HwMin,
            Max = HwMax,
        }),
    ];

    public CtlResult TryGetFrequencyRange(nint domain, out CtlFreqRange range)
    {
        Reads++;
        if (domain != GpuDomain)
        {
            range = default;
            return CtlResult.ErrorInvalidArgument;
        }

        if (ReadResult != CtlResult.Success)
        {
            range = default;
            return ReadResult;
        }

        range = new CtlFreqRange { Min = Min + ReadbackOffset, Max = Max + ReadbackOffset };
        return CtlResult.Success;
    }

    public CtlResult TrySetFrequencyRange(nint domain, double minMhz, double maxMhz)
    {
        if (domain != GpuDomain)
        {
            return CtlResult.ErrorInvalidArgument;
        }

        Writes.Add((minMhz, maxMhz));
        bool restore = minMhz < 0 && maxMhz < 0;
        var rc = restore ? RestoreResult : WriteResult;
        if (rc != CtlResult.Success)
        {
            return rc;
        }

        if (restore)
        {
            Min = FactoryMin;
            if (!KeepCapOnRestore)
            {
                Max = FactoryMax;
            }

            return CtlResult.Success;
        }

        double newMin = minMhz <= 0 ? HwMin : minMhz;
        double newMax = maxMhz <= 0 ? HwMax : maxMhz;
        if (ClampWritesToFactoryMax)
        {
            newMax = Math.Min(newMax, FactoryMax);
            newMin = Math.Min(newMin, newMax);
        }

        if (newMin > newMax)
        {
            return CtlResult.ErrorInvalidArgument;
        }

        Min = newMin;
        Max = newMax;
        if (minMhz > 0 && minMhz == maxMhz)
        {
            PinLanded.Set();
        }

        return CtlResult.Success;
    }

    public CtlResult TryGetOcProperties(out CtlOcProperties properties)
    {
        properties = default; // Supported = 0: no overclock block, so no power limit
        return CtlResult.Success;
    }

    public CtlResult TrySetOverclockWaiver() => CtlResult.Success;

    public CtlResult TryGetOcPowerLimitV2(out double limit)
    {
        limit = 0;
        return CtlResult.ErrorUnsupportedFeature;
    }

    public CtlResult TrySetOcPowerLimitV2(double limit) => CtlResult.ErrorUnsupportedFeature;
}
