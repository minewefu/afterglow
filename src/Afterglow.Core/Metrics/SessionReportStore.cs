using System.Text.Json;

namespace Afterglow.Core.Metrics;

/// <summary>
/// One finished FPS capture session, tagged with the tuning that was applied —
/// the raw material for honest before/after comparisons. FPS statistics are
/// the service's trailing-window numbers at capture end (steady state);
/// telemetry values are averaged over the recorded session (up to the history
/// ring's ~10 minutes).
/// </summary>
public sealed record SessionReport
{
    public required string Application { get; init; }

    /// <summary>
    /// The GPU whose sensor averages are recorded here, so a saved session can
    /// never be read as another card's. The report used to carry no identity at
    /// all and was stamped with whatever card the title bar happened to show at
    /// Stop — which on a hybrid machine is routinely not the one that rendered.
    /// Null on reports written before this field existed.
    /// </summary>
    public string? GpuName { get; init; }

    /// <summary>Stable identity of that GPU (null on older reports).</summary>
    public string? GpuUuid { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required int DurationSeconds { get; init; }

    public double AvgFps { get; init; }

    public double Low1Fps { get; init; }

    public double P1Fps { get; init; }

    public long Frames { get; init; }

    // Nullable so "the sensor never reported" stays distinguishable from "the
    // sensor read 0". These were non-nullable doubles fed by accumulators that
    // added 0 for every missing sample while still counting it, so a card with
    // no memory-junction sensor persisted AvgMemJunctionC = 0.0 and the exports
    // printed it as a measured 0 °C. Old records deserialize with these absent,
    // which now reads as "not measured" rather than a fabricated zero.
    public double? AvgPowerW { get; init; }

    public double? AvgGpuTempC { get; init; }

    public double? AvgMemJunctionC { get; init; }

    // Nullable for the same reason as the averages above: a card with no
    // offset knob (Intel Arc) has no offset, and persisting 0 exported it as
    // "core 0 / mem 0 MHz" — a measured value for a knob that does not exist.
    public int? CoreOffsetMHz { get; init; }

    public int? MemOffsetMHz { get; init; }
}

/// <summary>Append-only JSONL persistence, newest kept at the tail; capped.</summary>
public sealed class SessionReportStore
{
    private const int MaxKept = 200;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    public SessionReportStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.Root, "sessions.jsonl");
    }

    /// <summary>
    /// Records written before these averages became nullable stored a literal 0
    /// for "the sensor never reported" — the exact fabrication the nullable type
    /// exists to prevent. A session cannot average 0 W or 0 °C while it was
    /// running, so a non-positive average in a stored record means "not
    /// measured" and is read back as null rather than as a measurement.
    /// </summary>
    private static SessionReport NormalizeLegacyAverages(SessionReport report) => report with
    {
        AvgPowerW = report.AvgPowerW > 0 ? report.AvgPowerW : null,
        AvgGpuTempC = report.AvgGpuTempC > 0 ? report.AvgGpuTempC : null,
        AvgMemJunctionC = report.AvgMemJunctionC > 0 ? report.AvgMemJunctionC : null,
    };

    public IReadOnlyList<SessionReport> LoadAll()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var reports = new List<SessionReport>();
            foreach (string line in File.ReadAllLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<SessionReport>(line, JsonOptions) is { } report)
                    {
                        reports.Add(NormalizeLegacyAverages(report));
                    }
                }
                catch (JsonException)
                {
                }
            }

            return reports;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Append(SessionReport report)
    {
        try
        {
            var all = LoadAll().ToList();
            all.Add(report);
            if (all.Count > MaxKept)
            {
                all.RemoveRange(0, all.Count - MaxKept);
            }

            string temp = _path + ".tmp";
            File.WriteAllLines(temp, all.Select(r => JsonSerializer.Serialize(r, JsonOptions)));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A metric cell: the value, or "—" when it was never measured.</summary>
    private static string Cell(double? value, string format) =>
        value is { } d ? d.ToString(format, System.Globalization.CultureInfo.InvariantCulture) : "—";

    /// <summary>An offset cell: signed MHz, or "—" for a card with no such knob.</summary>
    private static string Off(int? mhz) =>
        mhz is { } m ? m.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture) : "—";

    /// <summary>Markdown comparison table of the most recent sessions (newest first).</summary>
    public static string ToMarkdown(IReadOnlyList<SessionReport> reports)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("| App | When | Length | Avg FPS | 1% low | Avg W | GPU °C | Mem °C | Core | Mem |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in reports)
        {
            sb.AppendLine(FormattableString.Invariant(
                // "—" for a sensor that never reported: this table gets pasted
                // into reports, where a printed 0 reads as a real measurement.
                $"| {r.Application} | {r.StartedAt:MM-dd HH:mm} | {r.DurationSeconds / 60.0:F1} min | {r.AvgFps:F1} | {r.Low1Fps:F1} | {Cell(r.AvgPowerW, "F0")} | {Cell(r.AvgGpuTempC, "F0")} | {Cell(r.AvgMemJunctionC, "F0")} | {Off(r.CoreOffsetMHz)} | {Off(r.MemOffsetMHz)} |"));
        }

        return sb.ToString();
    }
}
