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
    /// NVIDIA driver version the pass ran on. Offset stability is partly a
    /// property of the driver's clock management, so a driver update makes the
    /// certification stale. Null on certifications from builds that predate
    /// this field — those stay valid rather than silently expiring.
    /// </summary>
    public string? DriverVersion { get; init; }
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
    /// A certification counts only when it was earned at the profile's current
    /// offsets — the values that actually get applied — and on the driver
    /// that is running now (an update changes clock management under the
    /// tuning, so old passes stop being evidence).
    /// </summary>
    public static bool IsValidFor(this ProfileCertification cert, TuningProfile profile) =>
        cert.CoreOffsetMHz == profile.CoreOffsetMHz &&
        cert.MemOffsetMHz == profile.MemOffsetMHz &&
        (cert.DriverVersion is null || CurrentDriverVersion is null ||
         string.Equals(cert.DriverVersion, CurrentDriverVersion, StringComparison.Ordinal));

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
