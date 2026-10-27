using System.Text.Json;
using Afterglow.Core.Diagnostics;

namespace Afterglow.Core.Tuning;

/// <summary>
/// The factory frequency range an Arc tuner verified for one card: the floor
/// and ceiling a released clamp landed on (R-17 follow-up). The tuner learned
/// them from its first release and kept them in memory only, so every new
/// process — each CLI command, each launch — read a factory floor above the
/// hardware minimum as a limit it could not explain until it had released
/// once more; <c>get</c> reported it as a clamp.
/// <para>
/// Staleness is keyed, not timed. A record answers only for the card it was
/// measured on (the tuner's record key, which carries the PCI location and
/// device id), for the frequency domain's own limits it was measured against,
/// and for the driver version it was measured under: the factory range is
/// the driver's to set, so a driver update re-measures it. A driver that does
/// not report its version gets no record at all. Anything that still slips
/// past the key — a firmware update under the same driver — is caught by the
/// tuner, which never fails a release on a recorded value alone (see
/// <see cref="ArcGpuTuner"/>); it adopts what the release shows and rewrites the record.
/// </para>
/// </summary>
public static class ReleasedRangeStore
{
    /// <summary>One card's verified release.</summary>
    /// <param name="Key">The tuner's record key the range was measured for.</param>
    /// <param name="DriverVersion">The driver version it was measured under.</param>
    /// <param name="HwMinMhz">The frequency domain's own minimum at the time.</param>
    /// <param name="HwMaxMhz">The frequency domain's own maximum at the time.</param>
    /// <param name="MinMhz">The floor the verified release landed on.</param>
    /// <param name="MaxMhz">The ceiling the verified release landed on.</param>
    /// <param name="VerifiedAt">When it was verified.</param>
    public sealed record ReleasedRange(
        string Key,
        string DriverVersion,
        double HwMinMhz,
        double HwMaxMhz,
        double MinMhz,
        double MaxMhz,
        DateTimeOffset VerifiedAt);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Lock Sync = new();

    /// <summary>The file a card's record lives in, beside its applied-state record.</summary>
    public static string PathFor(string key) =>
        Path.Combine(AppPaths.Root, $"released-range-{AppliedStateStore.FileSuffixFor(key)}.json");

    /// <summary>
    /// The range on record for <paramref name="key"/>, when it was measured on
    /// this card, against these domain limits, under this driver; otherwise null.
    /// </summary>
    public static ReleasedRange? Load(string key, string? driverVersion, double hwMinMhz, double hwMaxMhz)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(driverVersion))
        {
            return null;
        }

        ReleasedRange? range;
        lock (Sync)
        {
            try
            {
                string path = PathFor(key);
                if (!File.Exists(path))
                {
                    return null;
                }

                range = JsonSerializer.Deserialize<ReleasedRange>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        if (range is null || !string.Equals(range.Key, key, StringComparison.Ordinal))
        {
            return null; // another card's file under a colliding name, or nothing usable
        }

        if (!string.Equals(range.DriverVersion, driverVersion, StringComparison.Ordinal))
        {
            Log.Info($"The factory clock range recorded for {key} was measured under driver {range.DriverVersion}; " +
                     $"driver {driverVersion} is installed now, so it is measured again at the next release.");
            return null;
        }

        bool sameDomain = Math.Abs(range.HwMinMhz - hwMinMhz) < 0.5 && Math.Abs(range.HwMaxMhz - hwMaxMhz) < 0.5;
        bool plausible = range.MinMhz > 0 && range.MaxMhz > 0 && range.MinMhz <= range.MaxMhz
            && range.MinMhz >= hwMinMhz - 0.5 && range.MaxMhz <= hwMaxMhz + 0.5;
        return sameDomain && plausible ? range : null;
    }

    /// <summary>Records a verified release. A failed write loses only the hint; never throws.</summary>
    public static void Save(ReleasedRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        lock (Sync)
        {
            try
            {
                AppPaths.EnsureCreated();
                File.WriteAllText(PathFor(range.Key), JsonSerializer.Serialize(range, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The range is a hint for the next process; the release itself stands.
            }
        }
    }
}
