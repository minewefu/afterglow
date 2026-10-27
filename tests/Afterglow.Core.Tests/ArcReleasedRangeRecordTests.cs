using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;
using static Afterglow.Core.Tests.Fakes.ArcScenario;

namespace Afterglow.Core.Tests;

/// <summary>
/// The factory range an Arc release verified, remembered across processes
/// (R-17's remaining limit). R-17 taught the tuner to adopt a factory floor
/// above the hardware minimum at its first release, but only in memory: every
/// new process — each CLI command, each launch — reported that floor as a
/// clamp nobody had applied until it released once more. Each "process" here
/// is a new tuner on a new fake device in the factory state, sharing only the
/// data directory.
/// </summary>
[Collection("AppPaths")]
public sealed class ArcReleasedRangeRecordTests : IDisposable
{
    private const string Driver = "32.0.101.6979";

    private readonly StoreScope _store = new();

    public void Dispose() => _store.Dispose();

    /// <summary>A card whose factory floor sits above the hardware minimum, as Intel's header allows.</summary>
    private static FakeArcDevice RaisedFloorCard(string? driver = Driver, double factoryMin = 300, double hwMax = 2300) =>
        new() { HwMin = 100, HwMax = hwMax, FactoryMin = factoryMin, FactoryMax = hwMax, DriverVersion = driver };

    private static string[] RangeFiles() =>
        Directory.Exists(AppPaths.Root) ? Directory.GetFiles(AppPaths.Root, "released-range-*.json") : [];

    [Fact]
    public void A_floor_one_process_verified_reads_as_released_in_the_next()
    {
        var first = Tuner(RaisedFloorCard());
        Assert.NotNull(first.ReadCurrent().LockedCoreClockMHz); // unexplained before any release, as R-17 left it
        var unlock = first.ForceUnlock();
        Assert.True(unlock.Applied, unlock.Detail);
        Assert.Contains("factory floor", unlock.Detail, StringComparison.Ordinal);

        // A new process on the same card, under the same driver.
        var next = Tuner(RaisedFloorCard());

        Assert.Null(next.ReadCurrent().LockedCoreClockMHz);
        Assert.Null(next.BeginProbe().ForeignClampNote); // nothing unexplained to release before a sweep
        Assert.True(next.EndProbe().Applied);

        var again = Tuner(RaisedFloorCard()).ForceUnlock();
        Assert.True(again.Applied, again.Detail);
        Assert.Contains("(verified)", again.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ceiling_one_process_verified_reads_as_released_in_the_next()
    {
        static FakeArcDevice LowCeilingCard() => new() { FactoryMax = 2200, DriverVersion = Driver };

        Assert.True(Tuner(LowCeilingCard()).ForceUnlock().Applied);

        var next = Tuner(LowCeilingCard());
        Assert.Null(next.ReadCurrent().LockedCoreClockMHz);
        Assert.Equal(2200u, next.MaxLockableClockMHz);
    }

    [Fact]
    public void A_driver_update_measures_the_range_again()
    {
        Assert.True(Tuner(RaisedFloorCard()).ForceUnlock().Applied);

        var updated = Tuner(RaisedFloorCard(driver: "32.0.101.7000"));

        Assert.NotNull(updated.ReadCurrent().LockedCoreClockMHz); // the old driver's measurement does not carry over
        var unlock = updated.ForceUnlock();
        Assert.True(unlock.Applied, unlock.Detail);
        Assert.Contains("factory floor", unlock.Detail, StringComparison.Ordinal);

        // Re-recorded under the new driver.
        Assert.Null(Tuner(RaisedFloorCard(driver: "32.0.101.7000")).ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void Another_card_or_another_frequency_domain_does_not_inherit_the_range()
    {
        Assert.True(Tuner(RaisedFloorCard()).ForceUnlock().Applied);

        var otherCard = new ArcGpuTuner(RaisedFloorCard(), "INTEL-0000:03:00.0-E20B");
        Assert.NotNull(otherCard.ReadCurrent().LockedCoreClockMHz);

        var otherDomain = Tuner(RaisedFloorCard(hwMax: 2400));
        Assert.NotNull(otherDomain.ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void A_record_the_card_no_longer_matches_never_fails_a_release()
    {
        // Recorded at a 300 MHz floor; the same driver now restores 500 (a
        // firmware change the key cannot see). An in-process verified floor
        // would fail this release; a record must not, or every release would
        // fail from then on — the R-17 failure, made permanent.
        Assert.True(Tuner(RaisedFloorCard()).ForceUnlock().Applied);

        var changed = Tuner(RaisedFloorCard(factoryMin: 500));
        Assert.NotNull(changed.ReadCurrent().LockedCoreClockMHz); // 500 is above the recorded floor: unexplained
        var unlock = changed.ForceUnlock();

        Assert.True(unlock.Applied, unlock.Detail);
        Assert.Contains("factory floor", unlock.Detail, StringComparison.Ordinal);
        Assert.Contains("300 MHz floor recorded for this card under this driver no longer holds", unlock.Detail, StringComparison.Ordinal);

        // The record now says 500.
        Assert.Null(Tuner(RaisedFloorCard(factoryMin: 500)).ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void A_driver_without_a_version_records_nothing()
    {
        Assert.True(Tuner(RaisedFloorCard(driver: null)).ForceUnlock().Applied);
        Assert.True(Tuner(RaisedFloorCard(driver: "0.0.0.0")).ForceUnlock().Applied);

        Assert.Empty(RangeFiles());
        Assert.NotNull(Tuner(RaisedFloorCard(driver: null)).ReadCurrent().LockedCoreClockMHz);
    }

    [Fact]
    public void A_release_that_matches_the_record_rewrites_nothing()
    {
        Assert.True(Tuner(RaisedFloorCard()).ForceUnlock().Applied);
        string file = Assert.Single(RangeFiles());
        var written = File.GetLastWriteTimeUtc(file);
        File.SetLastWriteTimeUtc(file, written - TimeSpan.FromHours(1));

        Assert.True(Tuner(RaisedFloorCard()).ForceUnlock().Applied);

        Assert.Equal(written - TimeSpan.FromHours(1), File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void A_pin_left_on_a_recorded_card_is_still_a_pin()
    {
        // The record explains the factory floor, never a floor at the ceiling.
        Assert.True(Tuner(RaisedFloorCard()).ForceUnlock().Applied);
        var dev = RaisedFloorCard();
        dev.Min = 1500;
        dev.Max = 1500;

        Assert.Equal(1500u, Tuner(dev).ReadCurrent().LockedCoreClockMHz);
    }
}
