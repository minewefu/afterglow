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

    [Fact]
    public void Validate_reports_null_collections_instead_of_throwing()
    {
        // R-24: the apply engine validates whatever profile it is handed, so a
        // null that slipped in through JSON must come back as an error, not as
        // a NullReferenceException from inside the check.
        Assert.NotNull(new TuningProfile
        {
            Name = "x", FanMode = FanMode.Curve, FanCurve = new FanCurveConfig { Points = null! },
        }.Validate());
        Assert.NotNull(new TuningProfile
        {
            Name = "x", FanMode = FanMode.Curve, FanCurve = new FanCurveConfig { Points = [null!, new(90, 100)] },
        }.Validate());

        // A curve carried by a fixed-fan profile still reaches the Fans page
        // editor when the profile is applied, so its shape is checked too.
        Assert.NotNull(new TuningProfile
        {
            Name = "x", FanMode = FanMode.Fixed, FanCurve = new FanCurveConfig { Points = null! },
        }.Validate());
        Assert.NotNull(new TuningProfile { Name = "x", Certifications = null! }.Validate());
        Assert.NotNull(new TuningProfile { Name = "x", Certifications = [null!] }.Validate());
    }

    // R-25: PathFor folds every character Windows can't put in a file name to
    // '_', and NTFS ignores letter case, so distinct names can share one file.
    // Saving the second used to replace the first silently, after which Load
    // and Delete by the first name reached the second profile.
    [Theory]
    [InlineData("Game: Quiet", "Game_ Quiet")]
    [InlineData("Game_ Quiet", "Game: Quiet")]
    [InlineData("a/b", "a\\b")]
    [InlineData("Quiet", "quiet")]
    public void A_name_whose_file_holds_another_profile_is_refused(string first, string second)
    {
        var store = new ProfileStore(_dir);
        store.Save(new TuningProfile { Name = first, CoreOffsetMHz = 100 });

        var ex = Assert.Throws<InvalidOperationException>(
            () => store.Save(new TuningProfile { Name = second, CoreOffsetMHz = 500 }));
        Assert.Contains($"'{first}'", ex.Message, StringComparison.Ordinal);

        // The first profile is untouched and the only one answering to its name.
        Assert.Equal(100, store.Load(first)?.CoreOffsetMHz);
        Assert.Null(store.Load(second));
        Assert.Equal(first, Assert.Single(store.LoadAll()).Name);
        Assert.False(store.Delete(second));
        Assert.Equal(100, store.Load(first)?.CoreOffsetMHz);
    }

    [Fact]
    public void Resaving_the_same_name_still_updates_it_in_place()
    {
        var store = new ProfileStore(_dir);
        store.Save(new TuningProfile { Name = "Game: Quiet", CoreOffsetMHz = 100 });
        store.Save(new TuningProfile { Name = "Game: Quiet", CoreOffsetMHz = 200 });

        Assert.Equal(200, store.Load("Game: Quiet")?.CoreOffsetMHz);
        Assert.Single(Directory.GetFiles(_dir, "*.json"));
    }

    [Fact]
    public void A_collision_an_older_build_left_resolves_only_to_the_survivor()
    {
        // What an older build left after saving 'Game: Quiet' and then
        // 'Game_ Quiet': one file, holding the second profile. A startup,
        // game-rule or automation reference to the first name must now find
        // nothing rather than the other profile's tuning, and deleting by the
        // first name must not delete the survivor.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, "Game_ Quiet.json"), """{ "name": "Game_ Quiet", "coreOffsetMHz": 500 }""");
        var store = new ProfileStore(_dir);

        Assert.Null(store.Load("Game: Quiet"));
        Assert.False(store.Delete("Game: Quiet"));
        Assert.Equal(500, store.Load("Game_ Quiet")?.CoreOffsetMHz);
        Assert.True(store.Delete("Game_ Quiet"));
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void Files_written_under_the_underscore_mapping_keep_loading_and_deleting()
    {
        // No migration: the mapping is unchanged, so a file an earlier build
        // wrote stays where it is and keeps answering to its own name.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, "Game_ Quiet.json"), """{ "name": "Game: Quiet", "coreOffsetMHz": 100 }""");
        var store = new ProfileStore(_dir);

        Assert.Equal("Game: Quiet", Assert.Single(store.LoadAll()).Name);
        Assert.Equal(100, store.Load("Game: Quiet")?.CoreOffsetMHz);

        store.Save(new TuningProfile { Name = "Game: Quiet", CoreOffsetMHz = 150 });
        Assert.Equal(150, store.Load("Game: Quiet")?.CoreOffsetMHz);
        Assert.Single(Directory.GetFiles(_dir, "*.json"));

        Assert.True(store.Delete("Game: Quiet"));
        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
    }

    [Fact]
    public void A_profile_this_build_cannot_parse_still_guards_its_file()
    {
        // A profile from a newer build (a value this one doesn't know) fails
        // to deserialize, but it still names itself — a colliding name must
        // not overwrite it.
        Directory.CreateDirectory(_dir);
        string newer = Path.Combine(_dir, "Game_ Quiet.json");
        File.WriteAllText(newer, """{ "name": "Game: Quiet", "fanMode": "Hybrid" }""");
        var store = new ProfileStore(_dir);

        Assert.Throws<InvalidOperationException>(() => store.Save(new TuningProfile { Name = "Game_ Quiet" }));
        Assert.Contains("Hybrid", File.ReadAllText(newer), StringComparison.Ordinal);

        // A file that names nothing (not JSON at all) identifies no profile,
        // so saving over it still works, as before.
        File.WriteAllText(Path.Combine(_dir, "junk.json"), "{ not json ");
        store.Save(new TuningProfile { Name = "junk", CoreOffsetMHz = 50 });
        Assert.Equal(50, store.Load("junk")?.CoreOffsetMHz);
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
