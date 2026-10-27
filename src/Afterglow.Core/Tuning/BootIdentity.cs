using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Afterglow.Core.Tuning;

/// <summary>
/// When a fact was written, in terms of the OS boot it was written in: the
/// wall-clock moment and the system uptime at that moment. The two together
/// date the boot itself (<see cref="BootedAtUtc"/>) without any OS query.
/// </summary>
/// <param name="WrittenAtUtc">Wall-clock time of the write.</param>
/// <param name="UptimeMs">Milliseconds since the OS booted, at the write (<see cref="Environment.TickCount64"/>).</param>
/// <param name="InterruptTime">
/// The interrupt-time count at the write, in 100 ns units: the clock Windows
/// dates its last wake in (see <see cref="BootIdentity.SleptSince(BootStamp, BootClockReading)"/>).
/// Null on stamps written before it was recorded, or when the count could not be read.
/// </param>
public sealed record BootStamp(DateTimeOffset WrittenAtUtc, long UptimeMs, long? InterruptTime = null)
{
    /// <summary>The boot this stamp belongs to, as the wall clock dated it at the write.</summary>
    [JsonIgnore]
    public DateTimeOffset BootedAtUtc => WrittenAtUtc - TimeSpan.FromMilliseconds(UptimeMs);
}

/// <summary>
/// One reading of every clock the boot and sleep rules compare.
/// </summary>
/// <param name="UtcNow">The wall clock.</param>
/// <param name="UptimeMs">Milliseconds since boot, sleep included (<see cref="Environment.TickCount64"/>).</param>
/// <param name="InterruptTime">The interrupt-time count now, in 100 ns units (QueryInterruptTime); null when unreadable.</param>
/// <param name="LastWakeInterruptTime">
/// The interrupt-time count at the last system wake (CallNtPowerInformation,
/// LastWakeTime); 0 when the system has not slept since it booted, null when
/// it could not be read.
/// </param>
public readonly record struct BootClockReading(
    DateTimeOffset UtcNow, long UptimeMs, long? InterruptTime = null, long? LastWakeInterruptTime = null);

/// <summary>
/// Whether a persisted fact was written in the OS boot that is running now
/// (R-14), and whether the system has slept or hibernated since it was. NVML
/// has no getter for a clock lock, so the NVIDIA tuner keeps a shadow of the
/// lock it applied and persists it; the driver drops the lock at a restart,
/// the shadow did not, and the next launch reported — and a later memory-only
/// <c>set</c> re-applied — a lock the card no longer had.
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
/// Sleep and hibernation are a separate question. The uptime counter keeps
/// running through both, so a resume — including Windows Fast Startup's
/// "shut down", which hibernates the kernel session — is the same boot; but
/// whether the NVIDIA driver puts a clock lock back after one has not been
/// measured. <see cref="SleptSince(BootStamp, BootClockReading)"/> answers
/// whether one happened after a write: Windows dates its last wake in
/// interrupt time (CallNtPowerInformation, LastWakeTime: "the interrupt-time
/// count, in 100-nanosecond units, at the last system wake time"), and
/// interrupt time — unlike the unbiased count — includes the time spent asleep,
/// so it runs on across a resume and a wake later than the write's own count
/// is a sleep since the write.
/// </para>
/// <para>
/// Limits, stated rather than hidden: a wall clock that was wrong when the lock
/// was written and corrected before a restart can still misdate a write by the
/// size of that correction. Whether Windows moves LastWakeTime when a Modern
/// Standby (S0 low-power idle) session ends, rather than only on a resume
/// from S3 or S4, is not documented and has not been measured here; if it
/// does not, a lock written before Modern Standby is adopted as before.
/// </para>
/// </summary>
public static partial class BootIdentity
{
    /// <summary>
    /// Test seam: every clock the rules read. Tests that replace it sit in the
    /// serialised "AppPaths" collection and put it back.
    /// </summary>
    internal static Func<BootClockReading> Clock { get; set; } = SystemClock;

    internal static BootClockReading SystemClock() =>
        new(DateTimeOffset.UtcNow, Environment.TickCount64, ReadInterruptTime(), ReadLastWakeTime());

    /// <summary>A stamp for a write happening now.</summary>
    public static BootStamp Now()
    {
        var reading = Clock();
        return new BootStamp(reading.UtcNow, reading.UptimeMs, reading.InterruptTime);
    }

    /// <summary>The clocks now, for <see cref="SleptSince(BootStamp, BootClockReading)"/>.</summary>
    public static BootClockReading Read() => Clock();

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

    /// <summary>
    /// Whether the system slept or hibernated after <paramref name="written"/>,
    /// which must be a stamp from the running boot (<see cref="IsSameBoot"/>):
    /// true when Windows' last wake is later, in interrupt time, than the
    /// write. Null when the last wake could not be read. A stamp from before
    /// the interrupt time was recorded is compared by its uptime instead: the
    /// tick count and the interrupt time both count from boot with sleep
    /// included, and they agree to within a clock tick (measured 4 ms apart
    /// 31 hours into a boot on the development machine) — far less than any
    /// sleep takes.
    /// </summary>
    public static bool? SleptSince(BootStamp written, BootClockReading now)
    {
        ArgumentNullException.ThrowIfNull(written);
        if (now.LastWakeInterruptTime is not long wake)
        {
            return null;
        }

        long writtenAt = written.InterruptTime ?? (written.UptimeMs * TimeSpan.TicksPerMillisecond);
        return wake > writtenAt;
    }

    /// <summary>
    /// The same question for a record that carries only its wall-clock time:
    /// the last wake is dated on the wall clock (now, minus the interrupt time
    /// elapsed since it), and a write before that moment slept since.
    /// </summary>
    public static bool? SleptSince(DateTimeOffset writtenAtUtc, BootClockReading now)
    {
        if (now.LastWakeInterruptTime is not long wake)
        {
            return null;
        }

        if (wake <= 0)
        {
            return false; // no wake since this boot began
        }

        long interruptNow = now.InterruptTime ?? (now.UptimeMs * TimeSpan.TicksPerMillisecond);
        var wokeAtUtc = now.UtcNow - TimeSpan.FromTicks(Math.Max(0, interruptNow - wake));
        return writtenAtUtc < wokeAtUtc;
    }

    // The two OS reads. Both are plain queries: no privilege, no state change.
    // A missing export or a refused query answers null, once — the rules then
    // fall back as documented above — and is not retried on every lock read.
    private static int _interruptTimeUnavailable;
    private static int _wakeTimeUnavailable;

    /// <summary>POWER_INFORMATION_LEVEL.LastWakeTime (powerbase.h).</summary>
    private const int LastWakeTimeLevel = 14;

    private static long? ReadInterruptTime()
    {
        if (Volatile.Read(ref _interruptTimeUnavailable) != 0)
        {
            return null;
        }

        try
        {
            QueryInterruptTime(out ulong now);
            return (long)now;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Volatile.Write(ref _interruptTimeUnavailable, 1);
            return null;
        }
    }

    private static long? ReadLastWakeTime()
    {
        if (Volatile.Read(ref _wakeTimeUnavailable) != 0)
        {
            return null;
        }

        try
        {
            // NTSTATUS: STATUS_SUCCESS is 0; anything else leaves the value unknown.
            return CallNtPowerInformation(LastWakeTimeLevel, 0, 0, out ulong wake, sizeof(ulong)) == 0
                ? (long)wake
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Volatile.Write(ref _wakeTimeUnavailable, 1);
            return null;
        }
    }

    // Documented in kernel32, but exported (on Windows 11 26300) from KernelBase
    // through the realtime API set only; kernel32 has no such entry point.
    [LibraryImport("api-ms-win-core-realtime-l1-1-1.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void QueryInterruptTime(out ulong interruptTime);

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CallNtPowerInformation(
        int informationLevel, nint inputBuffer, uint inputBufferLength, out ulong outputBuffer, uint outputBufferLength);
}
