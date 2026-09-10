using Afterglow.Core.Interop.Igcl;

namespace Afterglow.Core.Tests.Fakes;

/// <summary>
/// An in-memory IGCL GPU frequency domain, modelling what a range write and a
/// factory restore do to the range the driver reads back. Its behaviours are
/// the ones MEASURED on the B390 (2026-09-04, see
/// docs/research/intel-driver-apis.md): a restore returns the full factory
/// range, a write above the factory ceiling settles at the ceiling, and a
/// readback may sit a fraction below the request. The one unmeasured shape it
/// still models — a factory ceiling below the domain maximum — is what the
/// IGCL header allows and discrete Arc reports may bring.
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
    /// The ceiling a factory restore lands at, and the ceiling a write settles
    /// at. Below <see cref="HwMax"/> this models a card whose factory ceiling
    /// sits under the domain maximum — the shape behind most phantom-clamp
    /// findings; on the B390 the two are equal.
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

    /// <summary>Result of every range read — the readback getter failing is a real tuner branch.</summary>
    public CtlResult ReadResult { get; set; } = CtlResult.Success;

    /// <summary>
    /// Added to every readback. MEASURED on the B390: a fractional request is
    /// truncated (1499.6 reads back 1499.0), so a negative fraction is the
    /// realistic value.
    /// </summary>
    public double ReadbackOffset { get; set; }

    /// <summary>Every range write, in order (restores appear as -1/-1).</summary>
    public List<(double Min, double Max)> Writes { get; } = [];

    /// <summary>Set when the first exact pin (min == max) is written; lets a test wait for a probe to land one.</summary>
    public ManualResetEventSlim PinLanded { get; } = new();

    /// <summary>The exact pins written so far, in order.</summary>
    public IEnumerable<double> PinnedTargets =>
        Writes.Where(w => w.Min > 0 && w.Min == w.Max).Select(w => w.Max);

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
            Max = FactoryMax;
            return CtlResult.Success;
        }

        // 0 = hardware limit; anything above the factory ceiling settles at it
        // (measured: 2400 reads back 2300, a 2400..2400 pin reads 2300..2300).
        double newMax = Math.Min(maxMhz <= 0 ? HwMax : maxMhz, FactoryMax);
        double newMin = Math.Min(minMhz <= 0 ? HwMin : minMhz, newMax);
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
