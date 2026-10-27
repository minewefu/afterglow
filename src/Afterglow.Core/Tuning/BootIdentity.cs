using System.Text.Json.Serialization;

namespace Afterglow.Core.Tuning;

/// <summary>
/// When a fact was written, in terms of the OS boot it was written in: the
/// wall-clock moment and the system uptime at that moment. The two together
/// date the boot itself (<see cref="BootedAtUtc"/>) without any OS query.
/// </summary>
/// <param name="WrittenAtUtc">Wall-clock time of the write.</param>
/// <param name="UptimeMs">Milliseconds since the OS booted, at the write (<see cref="Environment.TickCount64"/>).</param>
public sealed record BootStamp(DateTimeOffset WrittenAtUtc, long UptimeMs)
{
    /// <summary>The boot this stamp belongs to, as the wall clock dated it at the write.</summary>
    [JsonIgnore]
    public DateTimeOffset BootedAtUtc => WrittenAtUtc - TimeSpan.FromMilliseconds(UptimeMs);
}

/// <summary>
/// Whether a persisted fact was written in the OS boot that is running now
/// (R-14). NVML has no getter for a clock lock, so the NVIDIA tuner keeps a
/// shadow of the lock it applied and persists it; the driver drops the lock
/// at a restart, the shadow did not, and the next launch reported — and a
/// later memory-only <c>set</c> re-applied — a lock the card no longer had.
/// <para>
/// Boot identity is derived from the uptime counter rather than queried: a
/// record written in boot A carries A's uptime and wall time, and the running
/// boot is "now minus the current uptime". The rule needs no fixed tolerance
/// on the side that matters. A write in an earlier boot happened before that
/// boot ended, so before the current one began, and it is refused however
/// soon after either boot it happened. A write in this boot is accepted as
/// long as the wall clock has not been stepped forward, since the write, by
/// more than the uptime the write was made at — that uptime is the tolerance,
/// and it scales itself: NTP slews and steps are seconds, a lock is written
/// well after boot. A smaller uptime now than at the write is a restart
/// whatever the clocks say.
/// </para>
/// <para>
/// Limits, stated rather than hidden: the uptime counter keeps running through
/// sleep and hibernation, so a resume — including Windows Fast Startup's
/// "shut down", which hibernates the kernel session — counts as the same boot.
/// That is the session the driver lives in; whether the NVIDIA driver puts a
/// clock lock back after such a resume has not been measured. A wall clock
/// that was wrong when the lock was written and corrected before a restart can
/// still misdate a write by the size of that correction.
/// </para>
/// </summary>
public static class BootIdentity
{
    /// <summary>
    /// Test seam: the wall clock and the uptime counter. Tests that replace it
    /// sit in the serialised "AppPaths" collection and put it back.
    /// </summary>
    internal static Func<(DateTimeOffset UtcNow, long UptimeMs)> Clock { get; set; } = SystemClock;

    internal static (DateTimeOffset UtcNow, long UptimeMs) SystemClock() =>
        (DateTimeOffset.UtcNow, Environment.TickCount64);

    /// <summary>A stamp for a write happening now.</summary>
    public static BootStamp Now()
    {
        var (utcNow, uptimeMs) = Clock();
        return new BootStamp(utcNow, uptimeMs);
    }

    /// <summary>True when <paramref name="written"/> was made in the boot <paramref name="now"/> belongs to.</summary>
    public static bool IsSameBoot(BootStamp written, BootStamp now)
    {
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(now);

        // The counter only grows within a boot; going backwards is a restart.
        if (now.UptimeMs < written.UptimeMs)
        {
            return false;
        }

        return written.WrittenAtUtc >= now.BootedAtUtc;
    }

    /// <summary>
    /// The fallback for a record written before stamps existed: only its
    /// wall-clock time is known, so it counts as this boot's when it was
    /// written after this boot began — the same comparison as
    /// <see cref="IsSameBoot"/>, without the uptime check.
    /// </summary>
    public static bool WasWrittenThisBoot(DateTimeOffset writtenAt, BootStamp now)
    {
        ArgumentNullException.ThrowIfNull(now);
        return writtenAt >= now.BootedAtUtc;
    }
}
