using System.Globalization;
using Afterglow.Core.Stress;

namespace Afterglow.Core.Profiles;

/// <summary>
/// One passed stability mode for a profile, pinned to the exact configuration
/// that was tested — editing the profile's tuning or fan policy silently
/// invalidates its certifications (they stay recorded but no longer count), and
/// so does a fix to the burn that earned them (<see cref="WorkloadRevision"/>).
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
    /// <see cref="ProfileFingerprint.TuningFingerprint"/> of the configuration
    /// that passed (R-03). The two offsets above were all that tied a pass to
    /// what it tested, so a lock, power limit, V/F table, fan policy or card
    /// change kept the approval. Null on certifications from builds that predate
    /// this field — those fall back to the offset comparison rather than
    /// silently expiring.
    /// </summary>
    public string? TuningFingerprint { get; init; }

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

    /// <summary>
    /// <see cref="GpuStressTest.WorkloadRevision"/> of the burn that earned a
    /// sustained, transitions or excursions pass; null on a VRAM pass, which
    /// runs its own test (<see cref="VramTest"/>).
    /// <para>
    /// Null on burn passes from builds that predate this field — and every
    /// released one of those ran revision 1, whose FMA chains overflowed to
    /// infinity, so a miscalculated result still matched the reference (R-01).
    /// Such a pass shows little more than that the driver did not reset. Unlike
    /// a missing <see cref="DriverVersion"/>, a missing revision therefore does
    /// NOT count: the evidence is known to be void, not merely unlabelled. The
    /// record stays stored so every surface can say why it stopped counting.
    /// </para>
    /// </summary>
    public int? WorkloadRevision { get; init; }
}

/// <summary>Where one mode's certification stands for a profile — for display, so a pass that stopped counting can say why.</summary>
public enum CertificationStanding
{
    /// <summary>Nothing was earned at the profile's current configuration.</summary>
    None = 0,

    /// <summary>A pass that counts (<see cref="CertificationModes.IsValidFor"/>).</summary>
    Valid,

    /// <summary>Earned at this configuration by a burn that could not see a miscalculation (R-01).</summary>
    OlderStressTest,

    /// <summary>Earned at this configuration, on a different driver.</summary>
    DriverChanged,
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
    /// The modes the burn engine (<see cref="GpuStressTest"/>) runs, so their
    /// passes carry its <see cref="ProfileCertification.WorkloadRevision"/>.
    /// The VRAM mode runs <see cref="VramTest"/>, which R-01 did not change, so
    /// its passes are neither stamped nor invalidated by a burn revision.
    /// </summary>
    public static readonly IReadOnlyList<string> BurnModes = [Sustained, Transitions, Excursions];

    /// <summary>True for a mode the burn engine runs (case-insensitive, as mode lookups are).</summary>
    public static bool UsesBurnWorkload(string mode) =>
        BurnModes.Any(burn => string.Equals(burn, mode, StringComparison.OrdinalIgnoreCase));

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
    /// configuration — the values that actually get applied — on the driver
    /// that is running now (an update changes clock management under the
    /// tuning, so old passes stop being evidence), and, for a burn mode, by the
    /// current burn workload (<see cref="IsWorkloadCurrent"/>).
    /// </summary>
    public static bool IsValidFor(this ProfileCertification cert, TuningProfile profile) =>
        cert.MatchesConfigurationOf(profile) && IsDriverCurrent(cert) && cert.IsWorkloadCurrent();

    /// <summary>
    /// False for a burn-mode pass earned by an older burn workload than this
    /// build's <see cref="GpuStressTest.WorkloadRevision"/>, or by one that did
    /// not record its revision — every pass from before R-01's fix, which could
    /// not see a miscalculation. Deliberately stricter than the driver rule,
    /// which keeps an unlabelled record valid: an unlabelled driver might be the
    /// current one, but an unlabelled burn is known to be the broken one.
    /// Always true for the VRAM mode, which the burn revision does not cover.
    /// </summary>
    public static bool IsWorkloadCurrent(this ProfileCertification cert) =>
        !UsesBurnWorkload(cert.Mode) ||
        cert.WorkloadRevision is int revision && revision >= GpuStressTest.WorkloadRevision;

    /// <summary>
    /// The configuration half of <see cref="IsValidFor"/>: the pass was earned
    /// by exactly this profile's configuration (its fingerprint, R-03), or — for
    /// a certification recorded before fingerprints existed — at its offsets.
    /// </summary>
    public static bool MatchesConfigurationOf(this ProfileCertification cert, TuningProfile profile) =>
        cert.CoreOffsetMHz == profile.CoreOffsetMHz &&
        cert.MemOffsetMHz == profile.MemOffsetMHz &&
        (cert.TuningFingerprint is not { } fingerprint ||
         string.Equals(fingerprint, profile.TuningFingerprint(), StringComparison.Ordinal));

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
    /// Latest certification for a mode that matches the profile's configuration
    /// (see <see cref="MatchesConfigurationOf"/>), even if it was earned on a
    /// different driver or by an older burn — lets the UI distinguish "never
    /// certified" from "certified, but it no longer counts" (see
    /// <see cref="StandingOf"/>).
    /// </summary>
    public static ProfileCertification? OffsetMatchedCertification(this TuningProfile profile, string mode) =>
        profile.Certifications.LastOrDefault(c =>
            string.Equals(c.Mode, mode, StringComparison.OrdinalIgnoreCase) && c.MatchesConfigurationOf(profile));

    public static bool IsFullyCertified(this TuningProfile profile) =>
        All.All(mode => profile.ValidCertification(mode) is not null);

    /// <summary>
    /// Where a mode's certification stands for the profile. A pass at this
    /// configuration that does not count is reported by the reason that voids
    /// it; an older burn outranks a driver change, because re-running on the old
    /// driver would not rescue it.
    /// </summary>
    public static CertificationStanding StandingOf(this TuningProfile profile, string mode) =>
        profile.ValidCertification(mode) is not null
            ? CertificationStanding.Valid
            : profile.OffsetMatchedCertification(mode) is not { } matched
                ? CertificationStanding.None
                : matched.IsWorkloadCurrent()
                    ? CertificationStanding.DriverChanged
                    : CertificationStanding.OlderStressTest;

    /// <summary>
    /// Why a pass at the profile's configuration no longer counts, in the words
    /// every surface uses (the Profiles page, the CLI, MCP and the certifier's
    /// log); null when it counts or there is none.
    /// </summary>
    public static string? StaleReason(CertificationStanding standing) => standing switch
    {
        CertificationStanding.OlderStressTest => "earned with an older stress test, re-certify",
        CertificationStanding.DriverChanged => "earned on a different driver, re-certify",
        _ => null,
    };

    /// <summary>
    /// The Profiles page's certification text: each mode's mark (✓ and the
    /// day it passed, ⚠ when a stored pass no longer counts, — when there is
    /// none), then one line per reason a pass stopped counting, naming its
    /// modes. Without the reason, a profile the old burn certified would read
    /// as merely uncertified the day after an update.
    /// </summary>
    public static string StatusSummary(this TuningProfile profile)
    {
        var standings = All.Select(mode => (Mode: mode, Standing: profile.StandingOf(mode))).ToArray();
        string marks = string.Join("   ", standings.Select(s => s.Standing switch
        {
            CertificationStanding.Valid => $"{s.Mode} ✓ " +
                profile.ValidCertification(s.Mode)!.PassedAt.ToString("MM-dd", CultureInfo.InvariantCulture),
            CertificationStanding.None => $"{s.Mode} —",
            _ => $"{s.Mode} ⚠",
        }));
        var reasons = standings
            .Where(s => StaleReason(s.Standing) is not null)
            .GroupBy(s => s.Standing)
            .Select(g => $"⚠ {string.Join(", ", g.Select(s => s.Mode))}: {StaleReason(g.Key)}");
        return string.Join(Environment.NewLine, [marks, .. reasons]);
    }

    /// <summary>One mode's standing as a phrase for text surfaces (CLI, MCP).</summary>
    public static string Describe(this TuningProfile profile, string mode)
    {
        var standing = profile.StandingOf(mode);
        return standing switch
        {
            CertificationStanding.Valid => "passed " +
                profile.ValidCertification(mode)!.PassedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            CertificationStanding.None => "not certified at these settings",
            _ => StaleReason(standing)!,
        };
    }

    /// <summary>
    /// True when some mode was passed at this configuration only by a burn that
    /// predates the current workload — a profile that may have read "Certified"
    /// before R-01's fix, and now needs to say why it does not.
    /// </summary>
    public static bool HasCertificationFromOlderStressTest(this TuningProfile profile) =>
        All.Any(mode => profile.StandingOf(mode) == CertificationStanding.OlderStressTest);
}
