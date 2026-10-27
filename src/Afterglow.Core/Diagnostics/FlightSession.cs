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

    /// <summary>
    /// The last tuning state the recording names — offsets, clock lock, V/F
    /// points and card identity, as far as the recording states them. Null
    /// when the parsed lines carry no "offsets" marker at all.
    /// </summary>
    public FlightTuning? Tuning { get; private init; }

    public IReadOnlyList<string> Markers { get; private init; } = [];

    /// <summary>True when the parsed lines include an "offsets" marker.</summary>
    internal bool HasOffsetsMarker { get; private init; }

    /// <summary>How much of a session's end the crash analysis reads (~10 hours at 1 s polling).</summary>
    internal const int TailBytes = 4 * 1024 * 1024;

    public static FlightSession? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Only the end of a session says how it died. The full file is read
            // only when its tail never names the applied offsets — a recording
            // from before the recorder re-stated them per segment.
            var tail = Parse(ReadTailLines(path, TailBytes, out bool truncated));
            return truncated && !tail.HasOffsetsMarker ? Parse(File.ReadAllLines(path)) : tail;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cheap check for the normal case — the previous session ended on purpose:
    /// reads the last few KB instead of parsing what can be days of recording.
    /// False when unsure, so the caller falls back to the full analysis.
    /// </summary>
    public static bool EndedCleanly(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var lines = ReadTailLines(path, 4096, out _);
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    return IsCleanShutdownMarker(lines[i]);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static bool IsCleanShutdownMarker(string line)
    {
        int space = line.IndexOf(' ', StringComparison.Ordinal);
        return line.StartsWith('#') && space >= 2 &&
               long.TryParse(line.AsSpan(1, space - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
               line.AsSpan(space + 1).TrimEnd().SequenceEqual("clean-shutdown");
    }

    /// <summary>
    /// The complete lines in the last <paramref name="maxBytes"/> of a file. When
    /// the read starts mid-file, the first (partial) line is dropped.
    /// </summary>
    private static List<string> ReadTailLines(string path, int maxBytes, out bool truncated)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, stream.Length - maxBytes);
        truncated = start > 0;
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        if (truncated)
        {
            _ = reader.ReadLine();
        }

        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    public static FlightSession Parse(IReadOnlyList<string> lines)
    {
        var samples = new List<FlightSample>();
        var markers = new List<string>();
        bool clean = false;
        FlightTuning? tuning = null;
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

                if (!TryTimestamp(markerMs, out var markerAt))
                {
                    continue;   // a valid long, but no date FromUnixTimeMilliseconds accepts (F-34)
                }

                endedAt = markerAt;
                string text = line[(space + 1)..];
                markers.Add(text);

                if (text == "clean-shutdown")
                {
                    clean = true;
                }
                else if (text.StartsWith("offsets ", StringComparison.Ordinal))
                {
                    tuning = FlightTuning.Parse(text, tuning);
                }

                continue;
            }

            string[] fields = line.Split('|');
            if (fields.Length < 7 ||
                !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
            {
                continue;
            }

            if (!TryTimestamp(ms, out var ts))
            {
                continue;
            }

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
            CoreOffsetMHz = tuning?.CoreOffsetMHz ?? 0,
            MemOffsetMHz = tuning?.MemOffsetMHz ?? 0,
            Tuning = tuning,
            Markers = markers,
            HasOffsetsMarker = tuning is not null,
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

    private static bool TryTimestamp(long unixMs, out DateTimeOffset at)
    {
        // DateTimeOffset's own limits (years 0001-9999); anything outside would throw.
        const long Min = -62_135_596_800_000;
        const long Max = 253_402_300_799_999;
        at = unixMs is >= Min and <= Max ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs) : default;
        return unixMs is >= Min and <= Max;
    }

    private static uint? ParseU(string s) =>
        uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v) ? v : null;

    private static double? ParseD(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}
