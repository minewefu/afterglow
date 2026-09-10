namespace Afterglow.Core.Interop.Igcl;

/// <summary>
/// The slice of an IGCL device the Arc tuner drives: the GPU frequency domain
/// (enumerate, read the range back, write a range or a factory restore) and
/// the overclock power-limit block. <see cref="IgclDevice"/> is the driver;
/// the test suite substitutes an in-memory device so every clock-lock
/// scenario — a factory ceiling below the domain maximum, a refused write, a
/// readback the driver truncates — runs against the real tuner instead of
/// against an argument about it.
/// </summary>
public interface IArcDevice
{
    IReadOnlyList<(nint Handle, CtlFreqProperties Properties)> GetFrequencyDomains();

    CtlResult TryGetFrequencyRange(nint domain, out CtlFreqRange range);

    /// <summary>Clamps the domain to [minMhz, maxMhz]. 0 = hardware limit, -1 = factory default.</summary>
    CtlResult TrySetFrequencyRange(nint domain, double minMhz, double maxMhz);

    CtlResult TryGetOcProperties(out CtlOcProperties properties);

    CtlResult TrySetOverclockWaiver();

    CtlResult TryGetOcPowerLimitV2(out double limit);

    CtlResult TrySetOcPowerLimitV2(double limit);
}
