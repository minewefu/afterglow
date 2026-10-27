using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// The same-boot rule behind the NVIDIA clock-lock shadow (R-14): a write is
/// this boot's when the uptime has not gone backwards since it and it was made
/// after this boot began. Pure arithmetic over stamps; no clock is read.
/// </summary>
public class BootIdentityTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static BootStamp At(DateTimeOffset utc, TimeSpan uptime) => new(utc, (long)uptime.TotalMilliseconds);

    [Fact]
    public void Later_in_the_same_boot_is_the_same_boot()
    {
        var written = At(Noon, TimeSpan.FromHours(2));

        Assert.True(BootIdentity.IsSameBoot(written, written));
        Assert.True(BootIdentity.IsSameBoot(written, At(Noon + TimeSpan.FromDays(30), TimeSpan.FromHours(2) + TimeSpan.FromDays(30))));
    }

    [Fact]
    public void Clock_steps_smaller_than_the_uptime_at_the_write_are_tolerated()
    {
        var written = At(Noon, TimeSpan.FromMinutes(5));

        // The wall clock was stepped forward 4 min, or back an hour, since the write.
        Assert.True(BootIdentity.IsSameBoot(written, At(Noon + TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15))));
        Assert.True(BootIdentity.IsSameBoot(written, At(Noon - TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(15))));
    }

    [Fact]
    public void A_restart_is_another_boot_however_long_either_boot_has_been_up()
    {
        var written = At(Noon, TimeSpan.FromSeconds(40)); // a startup profile, seconds after login

        // Back up after 20 s off, and checked 40 s, 5 min or 3 days into the new boot.
        foreach (var upSince in new[] { TimeSpan.FromSeconds(40), TimeSpan.FromMinutes(5), TimeSpan.FromDays(3) })
        {
            var now = At(Noon + TimeSpan.FromSeconds(20) + upSince, upSince);
            Assert.False(BootIdentity.IsSameBoot(written, now), $"checked {upSince} into the new boot");
        }
    }

    [Fact]
    public void An_uptime_smaller_than_at_the_write_is_a_restart_whatever_the_wall_clock_says()
    {
        var written = At(Noon, TimeSpan.FromHours(2));

        Assert.False(BootIdentity.IsSameBoot(written, At(Noon + TimeSpan.FromHours(1), TimeSpan.FromHours(1))));
    }

    [Fact]
    public void An_unstamped_write_counts_only_when_made_after_this_boot_began()
    {
        var now = At(Noon, TimeSpan.FromHours(2)); // booted at 10:00

        Assert.True(BootIdentity.WasWrittenThisBoot(Noon - TimeSpan.FromHours(1), now));
        Assert.False(BootIdentity.WasWrittenThisBoot(Noon - TimeSpan.FromHours(3), now));
    }

    [Fact]
    public void The_stamp_round_trips_through_the_record_json()
    {
        var state = new AppliedStateStore.AppliedState(
            "Gaming", Noon, true, true, 2500, LockBoot: At(Noon, TimeSpan.FromHours(2)));

        string json = System.Text.Json.JsonSerializer.Serialize(state);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppliedStateStore.AppliedState>(json);

        Assert.Equal(state, back);
        Assert.DoesNotContain("BootedAtUtc", json, StringComparison.Ordinal);
    }
}
