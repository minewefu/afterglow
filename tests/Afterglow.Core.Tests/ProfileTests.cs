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
