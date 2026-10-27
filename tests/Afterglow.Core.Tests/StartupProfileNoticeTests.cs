using Afterglow.Core.Profiles;
using Afterglow.Core.Stress;

namespace Afterglow.Core.Tests;

/// <summary>
/// R2-B: a startup profile marked stable keeps applying when its passes came
/// from the burn before R-01's fix (decided), but startup now says so — once per
/// launch in the tray, and under the Settings picker — in the words the Profiles
/// page and CLI already use.
/// </summary>
public class StartupProfileNoticeTests
{
    private static readonly TuningProfile Daily = new()
    {
        Name = "Daily",
        GpuUuid = "GPU-A",
        CoreOffsetMHz = 100,
        MemOffsetMHz = 500,
        MarkedStable = true,
    };

    /// <summary>A pass at <see cref="Daily"/>'s configuration; no driver stamp, so only the burn revision decides.</summary>
    private static ProfileCertification Pass(string mode, int? revision) => new()
    {
        Mode = mode,
        PassedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        DurationSeconds = 90,
        CoreOffsetMHz = Daily.CoreOffsetMHz,
        MemOffsetMHz = Daily.MemOffsetMHz,
        TuningFingerprint = Daily.TuningFingerprint(),
        WorkloadRevision = revision,
    };

    private static ProfileCertification Current(string mode) =>
        Pass(mode, CertificationModes.UsesBurnWorkload(mode) ? GpuStressTest.WorkloadRevision : null);

    private static TuningProfile With(params ProfileCertification[] passes) => Daily with { Certifications = passes };

    [Fact]
    public void A_stable_profile_certified_by_the_old_burn_gets_the_re_certify_note()
    {
        // The certificates of a profile certified before R-01: every burn mode
        // unstamped, VRAM still valid.
        var profile = With(
            Pass(CertificationModes.Sustained, null), Pass(CertificationModes.Transitions, null),
            Pass(CertificationModes.Excursions, null), Current(CertificationModes.Vram));

        string? note = StartupProfileNotice.OlderStressTestNote(profile);

        Assert.Equal(
            "Its sustained, transitions and excursions passes were earned with an older stress test, " +
            "re-certify it on the Profiles page.",
            note);

        // The shared wording, so the tray matches the Profiles page and the CLI.
        Assert.Contains(CertificationModes.StaleReason(CertificationStanding.OlderStressTest)!, note, StringComparison.Ordinal);

        // Exactly when the Profiles page shows its "Re-certify" badge.
        Assert.True(profile.CertifiedWithOlderStressTest);
    }

    [Fact]
    public void One_old_pass_is_named_alone()
    {
        var profile = With(Pass(CertificationModes.Sustained, GpuStressTest.WorkloadRevision - 1), Current(CertificationModes.Vram));

        Assert.Equal(
            "Its sustained pass was earned with an older stress test, re-certify it on the Profiles page.",
            StartupProfileNotice.OlderStressTestNote(profile));
    }

    [Fact]
    public void The_tray_notice_is_the_applied_notice_with_the_note_in_one_message()
    {
        // One notice per launch: the tray rate-limits alerts by title, so a
        // second "Afterglow" alert would be dropped; the note rides on the first.
        var profile = With(Pass(CertificationModes.Sustained, null));

        Assert.Equal(
            "Startup profile 'Daily' applied. Its sustained pass was earned with an older stress test, " +
            "re-certify it on the Profiles page.",
            StartupProfileNotice.Applied(profile));

        // The tray balloon holds 255 characters (TrayService cuts the rest): the
        // longest note still leaves room for a profile name of about 100.
        string longest = StartupProfileNotice.Applied(With(
            Pass(CertificationModes.Sustained, null), Pass(CertificationModes.Transitions, null),
            Pass(CertificationModes.Excursions, null)));
        Assert.True(longest.Length - Daily.Name.Length <= 155, longest);
    }

    [Fact]
    public void Nothing_is_added_when_no_pass_is_from_the_old_burn()
    {
        var certified = With([.. CertificationModes.All.Select(Current)]);
        var never = With();
        var vramOnly = With(Pass(CertificationModes.Vram, null));

        foreach (var profile in new[] { certified, never, vramOnly })
        {
            Assert.Null(StartupProfileNotice.OlderStressTestNote(profile));
            Assert.Equal("Startup profile 'Daily' applied.", StartupProfileNotice.Applied(profile));
        }
    }

    [Fact]
    public void Old_passes_for_settings_the_profile_no_longer_has_are_not_reported()
    {
        // Edited since: those passes describe another configuration, which the
        // Profiles page shows as uncertified, not as "Re-certify".
        var edited = With(Pass(CertificationModes.Sustained, null)) with { CoreOffsetMHz = 150 };

        Assert.Null(StartupProfileNotice.OlderStressTestNote(edited));
        Assert.False(edited.CertifiedWithOlderStressTest);
    }

    [Fact]
    public void The_note_does_not_change_whether_startup_applies_the_profile()
    {
        // RestoreStartupState applies a profile because it is marked stable; the
        // old-burn passes are reported, and the mark stays.
        var profile = With(Pass(CertificationModes.Sustained, null));

        Assert.True(profile.MarkedStable);
        Assert.NotNull(StartupProfileNotice.OlderStressTestNote(profile));
    }
}
