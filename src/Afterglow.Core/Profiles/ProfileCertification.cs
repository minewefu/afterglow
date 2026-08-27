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
    /// A certification counts only when it was earned at the profile's current
    /// offsets — the values that actually get applied.
    /// </summary>
    public static bool IsValidFor(this ProfileCertification cert, TuningProfile profile) =>
        cert.CoreOffsetMHz == profile.CoreOffsetMHz && cert.MemOffsetMHz == profile.MemOffsetMHz;

    public static ProfileCertification? ValidCertification(this TuningProfile profile, string mode) =>
        profile.Certifications.LastOrDefault(c =>
            string.Equals(c.Mode, mode, StringComparison.OrdinalIgnoreCase) && c.IsValidFor(profile));

    public static bool IsFullyCertified(this TuningProfile profile) =>
        All.All(mode => profile.ValidCertification(mode) is not null);
}
