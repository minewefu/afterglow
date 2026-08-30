using Afterglow.Core.Fans;

namespace Afterglow.Core.Profiles;

public enum FanMode
{
    /// <summary>Firmware-controlled (driver default, incl. its zero-RPM behavior).</summary>
    Auto = 0,

    /// <summary>One fixed duty for all fans.</summary>
    Fixed = 1,

    /// <summary>Afterglow's software fan curve drives the fans.</summary>
    Curve = 2,
}

/// <summary>
/// One saved tuning configuration. All values are deltas/limits validated against
/// driver-reported ranges at apply time — a profile that exceeds what the current
/// GPU allows is clamped and reported, never silently applied.
/// </summary>
public sealed record TuningProfile
{
    public required string Name { get; init; }

    /// <summary>Core (graphics) clock offset in MHz.</summary>
    public int CoreOffsetMHz { get; init; }

    /// <summary>Memory clock offset in MHz.</summary>
    public int MemOffsetMHz { get; init; }

    /// <summary>Board power limit in watts; null = leave at default.</summary>
    public double? PowerLimitW { get; init; }

    /// <summary>GPU temperature limit in °C; null = leave at default.</summary>
    public uint? TempLimitC { get; init; }

    /// <summary>
    /// Fixed upper core clock in MHz (locked-clock undervolting); null = unlocked.
    /// Combined with a positive core offset this is the documented-API undervolt:
    /// the V/F curve shifts up, then the lock caps the boost at the target clock,
    /// which is now reached at a lower voltage.
    /// </summary>
    public uint? LockedCoreClockMHz { get; init; }

    /// <summary>Core voltage boost in percent (0–100), if the hardware exposes it; null = untouched.</summary>
    public uint? VoltageBoostPct { get; init; }

    public FanMode FanMode { get; init; } = FanMode.Auto;

    /// <summary>Duty used when <see cref="FanMode.Fixed"/>.</summary>
    public uint FixedFanPct { get; init; } = 50;

    /// <summary>Curve used when <see cref="FanMode.Curve"/>.</summary>
    public FanCurveConfig? FanCurve { get; init; }

    /// <summary>Per-point V/F curve offsets (MHz), keyed by curve point index; empty when unused.</summary>
    public IReadOnlyDictionary<int, int>? VfPointOffsetsMHz { get; init; }

    /// <summary>
    /// True when this profile was saved by a path that actually read the V/F
    /// table, so an empty <see cref="VfPointOffsetsMHz"/> means "no per-point
    /// offsets", not "this profile has no opinion". Only then may applying it
    /// remove a curve. Every profile built from <c>ReadCurrent</c> — the CLI's
    /// partial `set`, the MCP tuning tool, the stepper's per-step offset, the
    /// pre-game restore — leaves this false and can never delete a curve the
    /// user did not ask to lose.
    /// </summary>
    public bool CapturedVfPoints { get; init; }

    /// <summary>Set once the user (or the stability stepper) has validated this profile under load.</summary>
    public bool MarkedStable { get; init; }

    /// <summary>
    /// Stability modes this profile has passed (see <see cref="CertificationModes"/>).
    /// A certification only counts while the profile's offsets still match the
    /// values it was earned at.
    /// </summary>
    public IReadOnlyList<ProfileCertification> Certifications { get; init; } = [];

    /// <summary>All four modes passed at the current offsets (display convenience).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FullyCertified => this.IsFullyCertified();

    /// <summary>Free-form user notes.</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>
    /// NVML UUID of the GPU this profile was saved on. The apply engine refuses
    /// a profile stamped for a different card — the same offsets mean different
    /// things on different silicon. Null (pre-multi-GPU profiles) applies
    /// anywhere.
    /// </summary>
    public string? GpuUuid { get; init; }

    /// <summary>Display name of the GPU this profile was saved on.</summary>
    public string? GpuName { get; init; }

    /// <summary>
    /// True when this profile may land on the card with the given NVML UUID.
    /// The same rule the tuner's identity gate enforces: an unstamped profile
    /// (or a card the driver won't identify) applies anywhere, two known and
    /// different identities never mix. Callers that pick the target card
    /// themselves — automation aims at the card that breached — ask this first,
    /// so they can refuse before any knob moves instead of half-applying.
    /// </summary>
    public bool AppliesToGpu(string? gpuUuid) =>
        GpuUuid is null || gpuUuid is null ||
        string.Equals(GpuUuid, gpuUuid, StringComparison.OrdinalIgnoreCase);

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset ModifiedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>A profile that changes nothing (driver defaults).</summary>
    public static TuningProfile Defaults { get; } = new() { Name = "Defaults" };

    /// <summary>Returns a human-readable validation error, or null when the profile is sane.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "Profile needs a name.";
        }

        // Generous static bounds; the apply engine additionally clamps to the
        // driver-reported range for the actual GPU.
        if (CoreOffsetMHz is < -1500 or > 1500)
        {
            return "Core offset outside ±1500 MHz.";
        }

        if (MemOffsetMHz is < -4000 or > 6000)
        {
            return "Memory offset outside -4000..+6000 MHz.";
        }

        if (PowerLimitW is < 50 or > 2000)
        {
            return "Power limit outside 50..2000 W.";
        }

        if (TempLimitC is < 60 or > 100)
        {
            return "Temperature limit outside 60..100 °C.";
        }

        if (LockedCoreClockMHz is < 210 or > 4500)
        {
            return "Locked clock outside 210..4500 MHz.";
        }

        if (VoltageBoostPct is > 100)
        {
            return "Voltage boost outside 0..100 %.";
        }

        if (FanMode == FanMode.Fixed && FixedFanPct > 100)
        {
            return "Fixed fan duty outside 0..100 %.";
        }

        if (FanMode == FanMode.Curve)
        {
            if (FanCurve is null)
            {
                return "Curve mode selected but no curve defined.";
            }

            if (FanCurve.Validate() is string curveError)
            {
                return $"Fan curve: {curveError}";
            }
        }

        if (VfPointOffsetsMHz is { Count: > 0 } vfPoints)
        {
            foreach (var (index, offset) in vfPoints)
            {
                if (index is < 0 or > 254)
                {
                    return $"V/F point index {index} outside 0..254.";
                }

                if (offset is < -1500 or > 1500)
                {
                    return $"V/F point offset {offset} MHz outside ±1500 MHz.";
                }
            }
        }

        return null;
    }
}
