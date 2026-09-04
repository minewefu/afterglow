namespace Afterglow.Core.Profiles;

/// <summary>
/// One passed stability mode for a profile, pinned to the exact offsets that
/// were tested — editing the profile's clocks silently invalidates its
/// certifications (they stay recorded but no longer count).
/// </summary>
public sealed record ProfileCertification
{
    public required string Mode { get; init; }

    public required DateTimeOffset PassedAt { get; init; }

    public required int DurationSeconds { get; init; }

    /// <summary>Core offset that was applied while this mode passed.</summary>
    public required int CoreOffsetMHz { get; init; }

    /// <summary>Memory offset that was applied while this mode passed.</summary>
    public required int MemOffsetMHz { get; init; }

    /// <summary>Human-readable pass evidence ("9 transitions, 0 errors").</summary>
    public string Evidence { get; init; } = string.Empty;

    /// <summary>
    /// Driver version the pass ran on, for the GPU it ran on. Offset stability
    /// is partly a property of the driver's clock management, so a driver update
    /// makes the certification stale. Null on certifications from builds that
    /// predate this field — those stay valid rather than silently expiring.
    /// </summary>
    public string? DriverVersion { get; init; }

    /// <summary>
    /// UUID of the GPU this pass ran on, so staleness is judged against THAT
    /// card's driver.
    /// <para>
    /// The version was previously compared against one process-wide string that
    /// preferred NVML's. On a hybrid Intel + NVIDIA machine — the configuration
    /// this release exists to support — an Arc certification was therefore
    /// stamped with the NVIDIA driver version: an Intel driver update left it
    /// looking valid, and an NVIDIA update falsely invalidated it. Null on older
    /// records, which fall back to the global comparison as before.
    /// </para>
    /// </summary>
    public string? GpuUuid { get; init; }
}

/// <summary>The four certification modes and validity rules.</summary>
public static class CertificationModes
{
    public const string Sustained = "sustained";
    public const string Transitions = "transitions";
    public const string Excursions = "excursions";
    public const string Vram = "vram";

    public static readonly IReadOnlyList<string> All = [Sustained, Transitions, Excursions, Vram];

    /// <summary>
    /// Driver version running right now; set once at startup by
    /// <c>GpuManager</c>. Null (no hardware / demo mode) disables the driver
    /// staleness check rather than invalidating everything.
    /// </summary>
    public static string? CurrentDriverVersion { get; set; }

    /// <summary>
    /// Current driver version per GPU UUID, set at startup by <c>GpuManager</c>.
    /// Consulted first so each card's certifications are judged against its own
    /// driver stack.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DriverVersionByGpu { get; set; } =
        new Dictionary<string, string>();

    /// <summary>The driver version to compare a certification against.</summary>
    public static string? CurrentDriverFor(string? gpuUuid) =>
        gpuUuid is { Length: > 0 } uuid && DriverVersionByGpu.TryGetValue(uuid, out string? version)
            ? version
            : CurrentDriverVersion;

    /// <summary>
    /// A certification counts only when it was earned at the profile's current
    /// offsets — the values that actually get applied — and on the driver
    /// that is running now (an update changes clock management under the
    /// tuning, so old passes stop being evidence).
    /// </summary>
    public static bool IsValidFor(this ProfileCertification cert, TuningProfile profile) =>
        cert.CoreOffsetMHz == profile.CoreOffsetMHz &&
        cert.MemOffsetMHz == profile.MemOffsetMHz &&
        IsDriverCurrent(cert);

    private static bool IsDriverCurrent(ProfileCertification cert)
    {
        string? current = CurrentDriverFor(cert.GpuUuid);
        return cert.DriverVersion is null || current is null ||
               string.Equals(cert.DriverVersion, current, StringComparison.Ordinal);
    }

    public static ProfileCertification? ValidCertification(this TuningProfile profile, string mode) =>
        profile.Certifications.LastOrDefault(c =>
            string.Equals(c.Mode, mode, StringComparison.OrdinalIgnoreCase) && c.IsValidFor(profile));

    /// <summary>
    /// Latest certification for a mode that matches the profile's offsets,
    /// even if it was earned on a different driver — lets the UI distinguish
    /// "never certified" from "certified, but the driver changed".
    /// </summary>
    public static ProfileCertification? OffsetMatchedCertification(this TuningProfile profile, string mode) =>
        profile.Certifications.LastOrDefault(c =>
            string.Equals(c.Mode, mode, StringComparison.OrdinalIgnoreCase) &&
            c.CoreOffsetMHz == profile.CoreOffsetMHz && c.MemOffsetMHz == profile.MemOffsetMHz);

    public static bool IsFullyCertified(this TuningProfile profile) =>
        All.All(mode => profile.ValidCertification(mode) is not null);
}
