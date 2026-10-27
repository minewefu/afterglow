using System.Globalization;
using System.Text;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Diagnostics;

/// <summary>
/// A tuning write as the flight recording states it: a <c>write-start</c>
/// marker, put on disk before the driver call (see <see cref="IGpuTuner.WriteStarting"/>),
/// and a <c>write-end</c> marker once the call returned. A recording that ends
/// between the two ended inside the write.
/// <code>
/// write-start 7 apply applying profile 'Gaming' (core +150 MHz, lock 1500 MHz)
/// write-end 7
/// </code>
/// Older readers skip both: they look for <c>offsets</c> and <c>clean-shutdown</c> only.
/// </summary>
/// <param name="Id">Pairs the start with its end; unique within the recording's process.</param>
/// <param name="Kind">What the write changes; null for a kind this build does not know.</param>
/// <param name="What">The write in words, a present-participle phrase ("applying profile …").</param>
public sealed record FlightWrite(long Id, TuningWriteKind? Kind, string What)
{
    private const string StartPrefix = "write-start ";
    private const string EndPrefix = "write-end ";

    /// <summary>The marker for a write that is about to reach the driver.</summary>
    public static string StartMarkerText(TuningWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return string.Create(
            CultureInfo.InvariantCulture, $"{StartPrefix}{write.Id} {KindToken(write.Kind)} {OneLine(write.What)}");
    }

    /// <summary>The marker for a write whose driver call returned.</summary>
    public static string EndMarkerText(long id) => string.Create(CultureInfo.InvariantCulture, $"{EndPrefix}{id}");

    /// <summary>Reads a <c>write-start</c> marker's text.</summary>
    public static bool TryParseStart(string markerText, out FlightWrite write)
    {
        ArgumentNullException.ThrowIfNull(markerText);
        write = null!;
        if (!markerText.StartsWith(StartPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = markerText[StartPrefix.Length..].Split(' ', 3);
        if (parts.Length < 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id))
        {
            return false;
        }

        write = new FlightWrite(id, ParseKind(parts[1]), parts.Length > 2 ? parts[2] : "writing tuning to the card");
        return true;
    }

    /// <summary>Reads a <c>write-end</c> marker's text.</summary>
    public static bool TryParseEnd(string markerText, out long id)
    {
        ArgumentNullException.ThrowIfNull(markerText);
        id = 0;
        return markerText.StartsWith(EndPrefix, StringComparison.Ordinal)
            && long.TryParse(markerText.AsSpan(EndPrefix.Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    private static string KindToken(TuningWriteKind kind) => kind switch
    {
        TuningWriteKind.Apply => "apply",
        TuningWriteKind.Reset => "reset",
        TuningWriteKind.VfPoints => "vf-points",
        TuningWriteKind.Lock => "lock",
        TuningWriteKind.ProbePin => "probe-pin",
        _ => "other",
    };

    private static TuningWriteKind? ParseKind(string token) => token switch
    {
        "apply" => TuningWriteKind.Apply,
        "reset" => TuningWriteKind.Reset,
        "vf-points" => TuningWriteKind.VfPoints,
        "lock" => TuningWriteKind.Lock,
        "probe-pin" => TuningWriteKind.ProbePin,
        _ => null,
    };

    /// <summary>A marker is one line: a profile name may carry anything.</summary>
    private static string OneLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }

        return sb.ToString();
    }
}
