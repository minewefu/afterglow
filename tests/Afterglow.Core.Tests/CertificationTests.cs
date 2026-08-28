using Afterglow.Core.Profiles;
using Afterglow.Core.Stress;

namespace Afterglow.Core.Tests;

public class CertificationTests
{
    private static ProfileCertification Cert(string mode, int core, int mem) => new()
    {
        Mode = mode,
        PassedAt = DateTimeOffset.Now,
        DurationSeconds = 90,
        CoreOffsetMHz = core,
        MemOffsetMHz = mem,
    };

    private static TuningProfile Profile(int core, int mem, params ProfileCertification[] certs) => new()
    {
        Name = "test",
        CoreOffsetMHz = core,
        MemOffsetMHz = mem,
        Certifications = certs,
    };

    [Fact]
    public void Certification_counts_only_at_the_offsets_it_was_earned_at()
    {
        var profile = Profile(100, 500, Cert(CertificationModes.Sustained, 100, 500));

        Assert.NotNull(profile.ValidCertification(CertificationModes.Sustained));
    }

    [Fact]
    public void Editing_the_offsets_invalidates_existing_certifications()
    {
        var certified = Profile(100, 500, Cert(CertificationModes.Sustained, 100, 500));
        var edited = certified with { MemOffsetMHz = 2000 };

        Assert.Null(edited.ValidCertification(CertificationModes.Sustained));
        Assert.False(edited.IsFullyCertified());
    }

    [Fact]
    public void Fully_certified_requires_all_four_modes()
    {
        var threeOfFour = Profile(100, 500,
            Cert(CertificationModes.Sustained, 100, 500),
            Cert(CertificationModes.Transitions, 100, 500),
            Cert(CertificationModes.Excursions, 100, 500));

        Assert.False(threeOfFour.IsFullyCertified());

        var all = threeOfFour with
        {
            Certifications =
            [
                .. threeOfFour.Certifications,
                Cert(CertificationModes.Vram, 100, 500),
            ],
        };

        Assert.True(all.IsFullyCertified());
    }

    [Fact]
    public void Stale_and_fresh_certifications_can_coexist_and_only_fresh_counts()
    {
        var profile = Profile(100, 500,
            Cert(CertificationModes.Sustained, 75, 2000),     // earned at old offsets
            Cert(CertificationModes.Sustained, 100, 500));    // earned at current offsets

        var valid = profile.ValidCertification(CertificationModes.Sustained);
        Assert.NotNull(valid);
        Assert.Equal(100, valid.CoreOffsetMHz);
    }

    [Fact]
    public void Driver_update_invalidates_driver_pinned_certifications()
    {
        string? saved = CertificationModes.CurrentDriverVersion;
        try
        {
            CertificationModes.CurrentDriverVersion = "616.56";
            var profile = Profile(100, 500,
                Cert(CertificationModes.Sustained, 100, 500) with { DriverVersion = "610.88" });

            Assert.Null(profile.ValidCertification(CertificationModes.Sustained));
            // ...but the UI can still tell "driver changed" apart from "never certified".
            Assert.NotNull(profile.OffsetMatchedCertification(CertificationModes.Sustained));

            CertificationModes.CurrentDriverVersion = "610.88";
            Assert.NotNull(profile.ValidCertification(CertificationModes.Sustained));
        }
        finally
        {
            CertificationModes.CurrentDriverVersion = saved;
        }
    }

    [Fact]
    public void Legacy_certifications_without_a_driver_version_stay_valid()
    {
        string? saved = CertificationModes.CurrentDriverVersion;
        try
        {
            CertificationModes.CurrentDriverVersion = "616.56";
            var legacy = Profile(100, 500, Cert(CertificationModes.Sustained, 100, 500));

            Assert.NotNull(legacy.ValidCertification(CertificationModes.Sustained));

            // No known current driver (demo mode) never invalidates anything either.
            CertificationModes.CurrentDriverVersion = null;
            var pinned = Profile(100, 500,
                Cert(CertificationModes.Sustained, 100, 500) with { DriverVersion = "610.88" });
            Assert.NotNull(pinned.ValidCertification(CertificationModes.Sustained));
        }
        finally
        {
            CertificationModes.CurrentDriverVersion = saved;
        }
    }
}

public class VramPlanTests
{
    private const long GiB = 1L << 30;

    [Fact]
    public void Plan_stays_inside_budget_minus_reserve_and_dedicated_cap()
    {
        // 32 GiB card, 30 GiB budget, 2 GiB already in use.
        long[] plan = VramTest.PlanChunks(30 * GiB, 2 * GiB, 32 * GiB);

        long total = plan.Sum();
        Assert.True(total <= 30 * GiB - 2 * GiB - (1536L << 20));
        Assert.True(total <= 32 * GiB * 95 / 100);
        Assert.True(total >= 24 * GiB);                 // most of the card is actually covered
        Assert.All(plan, size => Assert.True(size <= GiB));
        Assert.All(plan, size => Assert.Equal(0, size % 16));
    }

    [Fact]
    public void Tiny_free_memory_yields_an_empty_plan_instead_of_a_fake_test()
    {
        long[] plan = VramTest.PlanChunks(2 * GiB, 1 * GiB, 8 * GiB);

        Assert.Empty(plan);
    }

    [Fact]
    public void Small_card_gets_a_proportional_plan()
    {
        // 8 GiB card, 7.5 GiB budget, 1 GiB in use.
        long[] plan = VramTest.PlanChunks(7680L << 20, 1 * GiB, 8 * GiB);

        Assert.NotEmpty(plan);
        Assert.True(plan.Sum() >= 4 * GiB);
    }
}
