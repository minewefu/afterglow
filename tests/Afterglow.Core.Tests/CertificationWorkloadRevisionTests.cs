using System.Text.Json;
using Afterglow.Cli;
using Afterglow.Core.Profiles;
using Afterglow.Core.Stress;

namespace Afterglow.Core.Tests;

/// <summary>
/// A burn-mode certification counts only when the current burn earned it. The
/// burn before R-01's fix overflowed to infinity, so a miscalculated result
/// still matched its reference: those passes prove little more than "no driver
/// reset". They stay stored — the record is history, and every surface must be
/// able to say why it stopped counting — but they are no longer evidence. The
/// VRAM mode runs its own test and is untouched by the burn's revision.
/// </summary>
public sealed class CertificationWorkloadRevisionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"afterglow-revision-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static readonly TuningProfile Daily = new()
    {
        Name = "daily",
        GpuUuid = "GPU-A",
        CoreOffsetMHz = 100,
        MemOffsetMHz = 500,
        PowerLimitW = 250,
    };

    /// <summary>
    /// A pass at <see cref="Daily"/>'s configuration. DriverVersion stays null, so
    /// the driver rule (and other tests' changes to its process-wide current
    /// driver) never enters these verdicts.
    /// </summary>
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

    private static TuningProfile With(params ProfileCertification[] passes) => Daily with { Certifications = passes };

    /// <summary>What this build's certifier writes for a mode.</summary>
    private static ProfileCertification Current(string mode) =>
        Pass(mode, CertificationModes.UsesBurnWorkload(mode) ? GpuStressTest.WorkloadRevision : null);

    [Fact]
    public void The_burn_modes_are_exactly_the_modes_that_are_not_the_VRAM_test()
    {
        // The certifier picks the engine and the stamp from this one predicate.
        Assert.Equal(
            CertificationModes.All.Where(mode => mode != CertificationModes.Vram),
            CertificationModes.BurnModes);
        Assert.True(CertificationModes.UsesBurnWorkload("Sustained"));   // modes compare case-insensitively
        Assert.False(CertificationModes.UsesBurnWorkload(CertificationModes.Vram));
    }

    [Theory]
    [InlineData(CertificationModes.Sustained)]
    [InlineData(CertificationModes.Transitions)]
    [InlineData(CertificationModes.Excursions)]
    public void A_burn_pass_recorded_before_workload_revisions_no_longer_counts_and_says_why(string mode)
    {
        var profile = With(Pass(mode, revision: null));

        Assert.False(profile.Certifications[0].IsValidFor(profile));
        Assert.Null(profile.ValidCertification(mode));
        Assert.Equal(CertificationStanding.OlderStressTest, profile.StandingOf(mode));
        Assert.Equal("earned with an older stress test, re-certify", profile.Describe(mode));
        Assert.True(profile.CertifiedWithOlderStressTest);

        // Kept, not deleted: it is still the profile's history.
        Assert.Single(profile.Certifications);
    }

    [Fact]
    public void A_burn_pass_from_an_older_revision_no_longer_counts()
    {
        var profile = With(Pass(CertificationModes.Sustained, GpuStressTest.WorkloadRevision - 1));

        Assert.Null(profile.ValidCertification(CertificationModes.Sustained));
        Assert.Equal(CertificationStanding.OlderStressTest, profile.StandingOf(CertificationModes.Sustained));
    }

    [Fact]
    public void A_burn_pass_from_the_current_revision_counts()
    {
        var profile = With(Pass(CertificationModes.Sustained, GpuStressTest.WorkloadRevision));

        Assert.NotNull(profile.ValidCertification(CertificationModes.Sustained));
        Assert.Equal(CertificationStanding.Valid, profile.StandingOf(CertificationModes.Sustained));
        Assert.Equal("passed 2026-09-01", profile.Describe(CertificationModes.Sustained));
        Assert.False(profile.CertifiedWithOlderStressTest);

        // A newer build's burn is at least as strict, so its passes count here too.
        var newer = With(Pass(CertificationModes.Sustained, GpuStressTest.WorkloadRevision + 1));
        Assert.NotNull(newer.ValidCertification(CertificationModes.Sustained));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void The_VRAM_pass_is_not_judged_by_the_burn_revision(int? revision)
    {
        // The full-VRAM mode runs VramTest, not the burn R-01 fixed.
        var profile = With(Pass(CertificationModes.Vram, revision));

        Assert.NotNull(profile.ValidCertification(CertificationModes.Vram));
        Assert.Equal(CertificationStanding.Valid, profile.StandingOf(CertificationModes.Vram));
        Assert.False(profile.CertifiedWithOlderStressTest);
    }

    [Fact]
    public void A_profile_certified_by_the_old_burn_is_no_longer_fully_certified_but_keeps_its_stable_mark()
    {
        var oldBurn = With([
            .. CertificationModes.BurnModes.Select(mode => Pass(mode, revision: null)),
            Current(CertificationModes.Vram),
        ]) with { MarkedStable = true };

        Assert.False(oldBurn.FullyCertified);
        Assert.True(oldBurn.CertifiedWithOlderStressTest);

        // The stable mark is the user's (or a past run's) approval, and the one
        // thing startup checks; nothing here clears it.
        Assert.True(oldBurn.MarkedStable);

        var recertified = oldBurn with { Certifications = [.. CertificationModes.All.Select(Current)] };
        Assert.True(recertified.FullyCertified);
        Assert.False(recertified.CertifiedWithOlderStressTest);
    }

    [Fact]
    public void A_pass_that_no_longer_matches_the_settings_is_reported_as_uncertified_not_as_an_old_burn()
    {
        // The configuration rule comes first: an old burn at other settings
        // is simply not a pass for these.
        var edited = With(Pass(CertificationModes.Sustained, revision: null)) with { CoreOffsetMHz = 150 };

        Assert.Equal(CertificationStanding.None, edited.StandingOf(CertificationModes.Sustained));
        Assert.False(edited.CertifiedWithOlderStressTest);
    }

    [Fact]
    public void An_old_burn_is_named_before_a_driver_change()
    {
        // Driver versions per card, on a card id no other test uses, so the
        // process-wide current driver other tests set never reaches this one.
        var saved = CertificationModes.DriverVersionByGpu;
        try
        {
            CertificationModes.DriverVersionByGpu = new Dictionary<string, string>(saved) { ["GPU-REV-TEST"] = "32.0.101.9000" };
            var onOldDriver = Pass(CertificationModes.Sustained, GpuStressTest.WorkloadRevision) with
            {
                GpuUuid = "GPU-REV-TEST",
                DriverVersion = "32.0.101.6000",
            };

            Assert.Equal(CertificationStanding.DriverChanged, With(onOldDriver).StandingOf(CertificationModes.Sustained));
            Assert.Equal("earned on a different driver, re-certify", With(onOldDriver).Describe(CertificationModes.Sustained));

            // Both stale: re-running on the old driver would not rescue a pass
            // the old burn earned, so that is the reason given.
            var both = onOldDriver with { WorkloadRevision = null };
            Assert.Equal(CertificationStanding.OlderStressTest, With(both).StandingOf(CertificationModes.Sustained));
        }
        finally
        {
            CertificationModes.DriverVersionByGpu = saved;
        }
    }

    [Fact]
    public void The_revision_survives_a_save_and_reload_and_the_VRAM_pass_has_none()
    {
        var store = new ProfileStore(_dir);
        store.Save(With([.. CertificationModes.All.Select(Current)]));

        string json = File.ReadAllText(store.PathFor(Daily.Name));
        Assert.Contains($"\"workloadRevision\": {GpuStressTest.WorkloadRevision}", json, StringComparison.Ordinal);

        var loaded = store.Load(Daily.Name)!;
        Assert.All(CertificationModes.BurnModes, mode =>
            Assert.Equal(GpuStressTest.WorkloadRevision, loaded.ValidCertification(mode)?.WorkloadRevision));
        Assert.Null(loaded.ValidCertification(CertificationModes.Vram)!.WorkloadRevision);
        Assert.True(loaded.FullyCertified);
    }

    /// <summary>A profile as 1.5.0 wrote it: certifications with neither a fingerprint nor a workload revision.</summary>
    private const string LegacyProfileJson = """
        {
          "name": "legacy",
          "coreOffsetMHz": 100,
          "memOffsetMHz": 500,
          "markedStable": true,
          "certifications": [
            { "mode": "sustained", "passedAt": "2026-09-01T12:00:00+00:00", "durationSeconds": 90,
              "coreOffsetMHz": 100, "memOffsetMHz": 500, "evidence": "1048576 dispatches, 0 errors" },
            { "mode": "transitions", "passedAt": "2026-09-01T12:02:00+00:00", "durationSeconds": 90,
              "coreOffsetMHz": 100, "memOffsetMHz": 500, "evidence": "9 clock transitions, 400 dispatches, 0 errors" },
            { "mode": "excursions", "passedAt": "2026-09-01T12:04:00+00:00", "durationSeconds": 90,
              "coreOffsetMHz": 100, "memOffsetMHz": 500, "evidence": "12 boost excursions, 0 errors" },
            { "mode": "vram", "passedAt": "2026-09-01T12:06:00+00:00", "durationSeconds": 90,
              "coreOffsetMHz": 100, "memOffsetMHz": 500, "evidence": "10.0 GiB × 2 rounds, 0 errors" }
          ]
        }
        """;

    [Fact]
    public void A_profile_written_before_workload_revisions_still_loads_with_its_records_kept()
    {
        var store = new ProfileStore(_dir);
        File.WriteAllText(store.PathFor("legacy"), LegacyProfileJson);

        var loaded = store.Load("legacy");
        Assert.NotNull(loaded);
        Assert.Single(store.LoadAll(), p => p.Name == "legacy");
        Assert.Empty(store.LastLoadErrors);

        Assert.Equal(4, loaded.Certifications.Count);
        Assert.All(loaded.Certifications, c => Assert.Null(c.WorkloadRevision));
        Assert.True(loaded.MarkedStable);

        // Before this rule, these four records read "Certified".
        Assert.False(loaded.FullyCertified);
        Assert.True(loaded.CertifiedWithOlderStressTest);
        Assert.All(CertificationModes.BurnModes, mode =>
            Assert.Equal(CertificationStanding.OlderStressTest, loaded.StandingOf(mode)));
        Assert.Equal(CertificationStanding.Valid, loaded.StandingOf(CertificationModes.Vram));
    }

    [Fact]
    public void A_stale_revision_record_round_trips_and_still_loads()
    {
        var store = new ProfileStore(_dir);
        store.Save(With(Pass(CertificationModes.Excursions, revision: 1)));

        var loaded = store.Load(Daily.Name);
        Assert.NotNull(loaded);
        Assert.Single(store.LoadAll());
        Assert.Equal(1, loaded.Certifications.Single().WorkloadRevision);
        Assert.Equal(CertificationStanding.OlderStressTest, loaded.StandingOf(CertificationModes.Excursions));
    }

    [Fact]
    public void The_Profiles_page_says_why_an_old_burn_pass_no_longer_counts()
    {
        var profile = With([
            .. CertificationModes.BurnModes.Select(mode => Pass(mode, revision: null)),
            Current(CertificationModes.Vram),
        ]);

        Assert.Equal(
            "sustained ⚠   transitions ⚠   excursions ⚠   vram ✓ 09-01" + Environment.NewLine +
            "⚠ sustained, transitions, excursions: earned with an older stress test, re-certify",
            profile.StatusSummary());

        // A certified profile, and one with nothing earned at these settings,
        // carry no explanation line.
        Assert.Equal(
            "sustained ✓ 09-01   transitions ✓ 09-01   excursions ✓ 09-01   vram ✓ 09-01",
            With([.. CertificationModes.All.Select(Current)]).StatusSummary());
        Assert.Equal("sustained —   transitions —   excursions —   vram —", Daily.StatusSummary());
    }

    [Fact]
    public void The_CLI_says_why_an_old_burn_pass_no_longer_counts()
    {
        var profile = With(
            Pass(CertificationModes.Sustained, revision: null),
            Current(CertificationModes.Transitions),
            Current(CertificationModes.Vram));

        var lines = CertifyCommand.StatusLines(profile);

        Assert.Equal("Certification of 'daily':", lines[0]);
        Assert.Contains("  sustained    earned with an older stress test, re-certify", lines);
        Assert.Contains("  transitions  passed 2026-09-01", lines);
        Assert.Contains("  excursions   not certified at these settings", lines);
        Assert.Contains("  vram         passed 2026-09-01", lines);
    }

    /// <summary>The MCP server's own serializer settings.</summary>
    private static readonly JsonSerializerOptions McpJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void MCP_list_profiles_reports_what_each_stored_certification_counts_for()
    {
        // The records are listed as stored, so without this an agent would
        // count four stored passes as a certified profile.
        var store = new ProfileStore(_dir);
        store.Save(With([
            .. CertificationModes.BurnModes.Select(mode => Pass(mode, revision: null)),
            Current(CertificationModes.Vram),
        ]));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(McpCommand.ListProfiles(store), McpJson));
        var body = document.RootElement;

        Assert.Equal(4, body.GetProperty("profiles")[0].GetProperty("certifications").GetArrayLength());
        var status = body.GetProperty("certification")[0];
        Assert.Equal("daily", status.GetProperty("name").GetString());
        Assert.False(status.GetProperty("fully_certified").GetBoolean());

        var sustained = status.GetProperty("modes").GetProperty(CertificationModes.Sustained);
        Assert.False(sustained.GetProperty("counts").GetBoolean());
        Assert.Equal("earned with an older stress test, re-certify", sustained.GetProperty("detail").GetString());

        Assert.True(status.GetProperty("modes").GetProperty(CertificationModes.Vram).GetProperty("counts").GetBoolean());
    }
}
