using System.Globalization;

namespace Afterglow.Core.Diagnostics;

public readonly record struct FlightSample(
    DateTimeOffset Timestamp,
    uint? CoreMHz,
    uint? MemMHz,
    uint? TempC,
    double? MemTempC,
    double? PowerW,
    uint? UtilPct);

/// <summary>
/// Parsed flight-recorder session with the load analysis the crash classifier
/// needs. Parsing is lenient: a torn final line (power cut mid-write) or any
/// malformed line is skipped rather than failing the whole analysis.
/// </summary>
public sealed class FlightSession
{
    /// <summary>Utilization at or above this counts as heavy load.</summary>
    private const uint HeavyUtilPct = 80;

    /// <summary>Consecutive heavy samples required to call it a sustained load.</summary>
    private const int HeavyRunLength = 5;

    public IReadOnlyList<FlightSample> Samples { get; private init; } = [];

    public bool CleanShutdown { get; private init; }

    /// <summary>Timestamp of the last line of any kind — the session's time of death.</summary>
    public DateTimeOffset? EndedAt { get; private init; }

    public int CoreOffsetMHz { get; private init; }

    public int MemOffsetMHz { get; private init; }

    public IReadOnlyList<string> Markers { get; private init; } = [];

    public static FlightSession? Load(string path)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return Parse(lines);
    }

    public static FlightSession Parse(IReadOnlyList<string> lines)
    {
        var samples = new List<FlightSample>();
        var markers = new List<string>();
        bool clean = false;
        int core = 0;
        int mem = 0;
        DateTimeOffset? endedAt = null;

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                int space = line.IndexOf(' ', StringComparison.Ordinal);
                if (space < 2 ||
                    !long.TryParse(line.AsSpan(1, space - 1), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out long markerMs))
                {
                    continue;   // header or malformed marker
                }

                endedAt = DateTimeOffset.FromUnixTimeMilliseconds(markerMs);
                string text = line[(space + 1)..];
                markers.Add(text);

                if (text == "clean-shutdown")
                {
                    clean = true;
                }
                else if (text.StartsWith("offsets ", StringComparison.Ordinal))
                {
                    foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (part.StartsWith("core=", StringComparison.Ordinal) &&
                            int.TryParse(part.AsSpan(5), NumberStyles.AllowLeadingSign,
                                CultureInfo.InvariantCulture, out int c))
                        {
                            core = c;
                        }
                        else if (part.StartsWith("mem=", StringComparison.Ordinal) &&
                            int.TryParse(part.AsSpan(4), NumberStyles.AllowLeadingSign,
                                CultureInfo.InvariantCulture, out int m))
                        {
                            mem = m;
                        }
                    }
                }

                continue;
            }

            string[] fields = line.Split('|');
            if (fields.Length < 7 ||
                !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
            {
                continue;
            }

            var ts = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            endedAt = ts;
            samples.Add(new FlightSample(
                ts,
                ParseU(fields[1]),
                ParseU(fields[2]),
                ParseU(fields[3]),
                ParseD(fields[4]),
                ParseD(fields[5]),
                ParseU(fields[6])));
        }

        return new FlightSession
        {
            Samples = samples,
            CleanShutdown = clean,
            EndedAt = endedAt,
            CoreOffsetMHz = core,
            MemOffsetMHz = mem,
            Markers = markers,
        };
    }

    /// <summary>
    /// True when a sustained heavy load ran right up to the end of the recording.
    /// </summary>
    public bool HeavyLoadAtEnd()
    {
        var (runEndIndex, _) = LastHeavyRun();
        return runEndIndex >= 0 && runEndIndex >= Samples.Count - 3;
    }

    /// <summary>
    /// Seconds between the end of the last sustained heavy load and the end of
    /// the recording; null when no sustained load was seen, 0 when it was still
    /// running at death.
    /// </summary>
    public double? SecondsSinceHeavyLoadEnded()
    {
        var (runEndIndex, _) = LastHeavyRun();
        if (runEndIndex < 0 || EndedAt is not { } end)
        {
            return null;
        }

        if (runEndIndex >= Samples.Count - 3)
        {
            return 0;
        }

        return Math.Max(0, (end - Samples[runEndIndex].Timestamp).TotalSeconds);
    }

    public FlightSample? LastSample() => Samples.Count > 0 ? Samples[^1] : null;

    private (int EndIndex, int Length) LastHeavyRun()
    {
        int bestEnd = -1;
        int bestLength = 0;
        int runStart = -1;

        for (int i = 0; i < Samples.Count; i++)
        {
            bool heavy = Samples[i].UtilPct is uint u && u >= HeavyUtilPct;
            if (heavy)
            {
                if (runStart < 0)
                {
                    runStart = i;
                }

                int length = i - runStart + 1;
                if (length >= HeavyRunLength)
                {
                    bestEnd = i;
                    bestLength = length;
                }
            }
            else
            {
                runStart = -1;
            }
        }

        return (bestEnd, bestLength);
    }

    private static uint? ParseU(string s) =>
        uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v) ? v : null;

    private static double? ParseD(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}
