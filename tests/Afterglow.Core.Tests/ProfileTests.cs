using Afterglow.Core.Fans;
using Afterglow.Core.Profiles;

namespace Afterglow.Core.Tests;

public class ProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afterglow-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Profile_roundtrips_through_store()
    {
        var store = new ProfileStore(_dir);
        var profile = new TuningProfile
        {
            Name = "Quiet undervolt",
            CoreOffsetMHz = 150,
            MemOffsetMHz = 500,
            PowerLimitW = 450,
            LockedCoreClockMHz = 2600,
            FanMode = FanMode.Curve,
            FanCurve = new FanCurveConfig { TempSource = FanTempSource.HotSpot },
            Notes = "test",
        };

        store.Save(profile);
        var loaded = store.Load("Quiet undervolt");

        Assert.NotNull(loaded);
        Assert.Equal(profile.CoreOffsetMHz, loaded.CoreOffsetMHz);
        Assert.Equal(profile.MemOffsetMHz, loaded.MemOffsetMHz);
        Assert.Equal(profile.PowerLimitW, loaded.PowerLimitW);
        Assert.Equal(profile.LockedCoreClockMHz, loaded.LockedCoreClockMHz);
        Assert.Equal(FanMode.Curve, loaded.FanMode);
        Assert.NotNull(loaded.FanCurve);
        Assert.Equal(FanTempSource.HotSpot, loaded.FanCurve.TempSource);
        Assert.Equal(FanCurveConfig.DefaultPoints.Count, loaded.FanCurve.Points.Count);
    }

    [Fact]
    public void Save_rejects_invalid_profiles()
    {
        var store = new ProfileStore(_dir);
        Assert.Throws<InvalidOperationException>(
            () => store.Save(new TuningProfile { Name = "bad", CoreOffsetMHz = 99999 }));
    }

    [Fact]
    public void Corrupt_files_are_reported_not_fatal()
    {
        var store = new ProfileStore(_dir);
        store.Save(new TuningProfile { Name = "good" });
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json ");

        var all = store.LoadAll();

        Assert.Single(all);
        Assert.Single(store.LastLoadErrors);
    }

    [Fact]
    public void Profile_names_are_sanitized_for_filenames()
    {
        var store = new ProfileStore(_dir);
        var profile = new TuningProfile { Name = "a/b:c*d" };
        store.Save(profile);
        Assert.True(File.Exists(Path.Combine(_dir, "a_b_c_d.json")));
    }

    [Fact]
    public void Validate_bounds()
    {
        Assert.Null(new TuningProfile { Name = "ok" }.Validate());
        Assert.NotNull(new TuningProfile { Name = "" }.Validate());
        Assert.NotNull(new TuningProfile { Name = "x", MemOffsetMHz = 7000 }.Validate());
        Assert.NotNull(new TuningProfile { Name = "x", TempLimitC = 40 }.Validate());
        Assert.NotNull(new TuningProfile { Name = "x", FanMode = FanMode.Curve }.Validate());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The identity predicate the automation profile action consults
/// before it applies to the card that breached.</summary>
public class ProfileGpuTargetingTests
{
    [Fact]
        public void Profile_stamped_for_another_card_does_not_apply_to_it()
        {
            const string cardA = "GPU-aaaa1111-2222-3333-4444-555566667777";
            const string cardB = "GPU-bbbb1111-2222-3333-4444-555566667777";
            var profile = new TuningProfile { Name = "Throttle", GpuUuid = cardA, GpuName = "RTX 5090" };
    
            // The card it was saved on, whatever the driver's casing.
            Assert.True(profile.AppliesToGpu(cardA));
            Assert.True(profile.AppliesToGpu(cardA.ToUpperInvariant()));
    
            // The other card in the box — an automation breach here must be refused,
            // not silently redirected to card A.
            Assert.False(profile.AppliesToGpu(cardB));
        }

    [Fact]
        public void Unstamped_profile_or_unidentified_card_applies_anywhere()
        {
            const string card = "GPU-aaaa1111-2222-3333-4444-555566667777";
            var legacy = new TuningProfile { Name = "Pre-multi-GPU" };
            var stamped = new TuningProfile { Name = "Throttle", GpuUuid = card };
    
            // Profiles saved before UUID stamping apply to any card.
            Assert.True(legacy.AppliesToGpu(card));
            Assert.True(legacy.AppliesToGpu(null));
    
            // A card whose driver reports no UUID cannot be told apart, so the gate
            // opens — exactly what GpuTuner.Apply's identity check does.
            Assert.True(stamped.AppliesToGpu(null));
        }
}
