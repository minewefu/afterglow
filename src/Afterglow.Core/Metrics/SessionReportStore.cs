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

    public required DateTimeOffset StartedAt { get; init; }

    public required int DurationSeconds { get; init; }

    public double AvgFps { get; init; }

    public double Low1Fps { get; init; }

    public double P1Fps { get; init; }

    public long Frames { get; init; }

    public double AvgPowerW { get; init; }

    public double AvgGpuTempC { get; init; }

    public double AvgMemJunctionC { get; init; }

    public int CoreOffsetMHz { get; init; }

    public int MemOffsetMHz { get; init; }
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
                        reports.Add(report);
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

    /// <summary>Markdown comparison table of the most recent sessions (newest first).</summary>
    public static string ToMarkdown(IReadOnlyList<SessionReport> reports)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("| App | When | Length | Avg FPS | 1% low | Avg W | GPU °C | Mem °C | Core | Mem |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in reports)
        {
            sb.AppendLine(FormattableString.Invariant(
                $"| {r.Application} | {r.StartedAt:MM-dd HH:mm} | {r.DurationSeconds / 60.0:F1} min | {r.AvgFps:F1} | {r.Low1Fps:F1} | {r.AvgPowerW:F0} | {r.AvgGpuTempC:F0} | {r.AvgMemJunctionC:F0} | {r.CoreOffsetMHz:+0;-0;0} | {r.MemOffsetMHz:+0;-0;0} |"));
        }

        return sb.ToString();
    }
}
