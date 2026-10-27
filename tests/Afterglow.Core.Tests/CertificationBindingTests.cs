using Afterglow.Core.Fans;
using Afterglow.Core.Profiles;

namespace Afterglow.Core.Tests;

/// <summary>
/// A certification is bound to the configuration that earned it, by content
/// fingerprint (R-03) — not to the name of the profile, and not merely to its
/// two clock offsets.
/// </summary>
public sealed class CertificationBindingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"afterglow-binding-{Guid.NewGuid():N}");

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

    private static readonly TuningProfile Tested = new()
    {
        Name = "daily",
        GpuUuid = "GPU-A",
        CoreOffsetMHz = 100,
        MemOffsetMHz = 500,
        PowerLimitW = 287.5,
        TempLimitC = 83,
        LockedCoreClockMHz = 2000,
        VoltageBoostPct = 20,
        VfPointOffsetsMHz = new Dictionary<int, int> { [40] = 90, [41] = 75 },
        CapturedVfPoints = true,
        FanMode = FanMode.Fixed,
        FixedFanPct = 40,
    };

    private static ProfileCertification EarnedBy(TuningProfile profile, string mode = CertificationModes.Sustained) => new()
    {
        Mode = mode,
        PassedAt = DateTimeOffset.Now,
        DurationSeconds = 90,
        CoreOffsetMHz = profile.CoreOffsetMHz,
        MemOffsetMHz = profile.MemOffsetMHz,
        TuningFingerprint = profile.TuningFingerprint(),
    };

    private static TuningProfile Changed(string knob) => knob switch
    {
        "card" => Tested with { GpuUuid = "GPU-B" },
        "core" => Tested with { CoreOffsetMHz = 101 },
        "mem" => Tested with { MemOffsetMHz = 501 },
        "power" => Tested with { PowerLimitW = 287.6 },
        "power removed" => Tested with { PowerLimitW = null },
        "temp" => Tested with { TempLimitC = 84 },
        "lock removed" => Tested with { LockedCoreClockMHz = null },
        "lock" => Tested with { LockedCoreClockMHz = 2010 },
        "boost" => Tested with { VoltageBoostPct = 21 },
        "vf point" => Tested with { VfPointOffsetsMHz = new Dictionary<int, int> { [40] = 90, [41] = 76 } },
        "vf captured" => Tested with { CapturedVfPoints = false },
        "fan mode" => Tested with { FanMode = FanMode.Auto },
        "fan duty" => Tested with { FixedFanPct = 45 },
        "fan curve" => Tested with { FanMode = FanMode.Curve, FanCurve = new FanCurveConfig() },
        _ => throw new ArgumentOutOfRangeException(nameof(knob), knob, null),
    };

    [Theory]
    [InlineData("card")]
    [InlineData("core")]
    [InlineData("mem")]
    [InlineData("power")]
    [InlineData("power removed")]
    [InlineData("temp")]
    [InlineData("lock removed")]
    [InlineData("lock")]
    [InlineData("boost")]
    [InlineData("vf point")]
    [InlineData("vf captured")]
    [InlineData("fan mode")]
    [InlineData("fan duty")]
    [InlineData("fan curve")]
    public void Any_change_to_what_the_profile_applies_changes_the_fingerprint(string knob)
    {
        var changed = Changed(knob);
        Assert.NotEqual(Tested.TuningFingerprint(), changed.TuningFingerprint());

        // ...and with it, the certification stops counting — the review's
        // "lock removed, still certified" and "transplanted to card B, still
        // certified" cases among them.
        Assert.False(EarnedBy(Tested).IsValidFor(changed), $"a certification survived a change to the {knob}");
    }

    [Fact]
    public void Changes_that_are_not_configuration_leave_the_fingerprint_alone()
    {
        var same = Tested with
        {
            Name = "renamed",
            Notes = "ran fine in three games",
            GpuName = "Some GPU",
            GpuUuid = "gpu-a",                       // identity compares case-insensitively
            MarkedStable = true,
            Certifications = [EarnedBy(Tested)],
            CreatedAt = DateTimeOffset.UnixEpoch,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            FanCurve = new FanCurveConfig(),         // carried, but the fixed fan mode does not use it
            VfPointOffsetsMHz = new Dictionary<int, int> { [41] = 75, [40] = 90 },   // same table, other order
        };

        Assert.Equal(Tested.TuningFingerprint(), same.TuningFingerprint());
        Assert.True(EarnedBy(Tested).IsValidFor(same));
    }

    [Fact]
    public void The_fingerprint_survives_a_save_and_reload()
    {
        // It is compared against profiles read back from disk, so a value that
        // serialised differently would invalidate every certification it made.
        var store = new ProfileStore(_dir);
        var curved = Tested with
        {
            FanMode = FanMode.Curve,
            FanCurve = new FanCurveConfig { HysteresisC = 2.5, Points = [new(30, 30), new(66.6, 47.3), new(90, 100)] },
        };
        store.Save(curved);

        Assert.Equal(curved.TuningFingerprint(), store.Load(curved.Name)!.TuningFingerprint());
    }

    [Fact]
    public void A_certification_from_before_fingerprints_is_still_judged_by_its_offsets()
    {
        var legacy = EarnedBy(Tested) with { TuningFingerprint = null };

        Assert.True(legacy.IsValidFor(Tested with { LockedCoreClockMHz = null }));
        Assert.False(legacy.IsValidFor(Tested with { CoreOffsetMHz = 101 }));
    }

    [Fact]
    public void Full_certification_needs_all_four_modes_earned_by_this_configuration()
    {
        var certified = Tested with
        {
            Certifications = [.. CertificationModes.All.Select(mode => EarnedBy(Tested, mode))],
        };

        Assert.True(certified.FullyCertified);
        Assert.False((certified with { LockedCoreClockMHz = null }).FullyCertified);

        // The "driver changed" view uses the same configuration test.
        Assert.Null((certified with { FixedFanPct = 45 }).OffsetMatchedCertification(CertificationModes.Sustained));
    }
}
