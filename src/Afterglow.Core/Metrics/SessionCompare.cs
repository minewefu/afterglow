using System.Globalization;
using System.Text;

namespace Afterglow.Core.Metrics;

/// <summary>
/// Delta report between two recorded FPS sessions (older = baseline, newer =
/// candidate). Pure arithmetic over what was actually measured — no
/// projection, no smoothing.
/// </summary>
public static class SessionCompare
{
    /// <summary>Orders the pair by start time and produces the on-page summary.</summary>
    public static string Describe(SessionReport a, SessionReport b)
    {
        var (older, newer) = Order(a, b);
        var sb = new StringBuilder();

        if (!string.Equals(older.Application, newer.Application, StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(FormattableString.Invariant(
                $"⚠ Different applications ({older.Application} vs {newer.Application}) — FPS deltas are not comparable."));
        }

        sb.AppendLine(FormattableString.Invariant(
            $"A (baseline): {older.Application} {older.StartedAt:MM-dd HH:mm} · core {Off(older.CoreOffsetMHz)} / mem {Off(older.MemOffsetMHz)} MHz"));
        sb.AppendLine(FormattableString.Invariant(
            $"B (newer):    {newer.Application} {newer.StartedAt:MM-dd HH:mm} · core {Off(newer.CoreOffsetMHz)} / mem {Off(newer.MemOffsetMHz)} MHz"));
        sb.AppendLine(Delta("Avg FPS", older.AvgFps, newer.AvgFps, "F1", percentOfBase: true));
        sb.AppendLine(Delta("1% low", older.Low1Fps, newer.Low1Fps, "F1", percentOfBase: true));
        sb.AppendLine(Delta("Board power", older.AvgPowerW, newer.AvgPowerW, "F0", unit: " W"));
        sb.AppendLine(Delta("GPU temp", older.AvgGpuTempC, newer.AvgGpuTempC, "F1", unit: " °C"));
        if (older.AvgMemJunctionC is not null && newer.AvgMemJunctionC is not null)
        {
            sb.AppendLine(Delta("Mem junction", older.AvgMemJunctionC, newer.AvgMemJunctionC, "F1", unit: " °C"));
        }

        if (older.AvgPowerW is > 1 and { } olderW && newer.AvgPowerW is > 1 and { } newerW)
        {
            sb.AppendLine(Delta(
                "FPS per watt", older.AvgFps / olderW, newer.AvgFps / newerW,
                "F3", percentOfBase: true));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Markdown table form of the same comparison, for pasting.</summary>
    public static string ToMarkdown(SessionReport a, SessionReport b)
    {
        var (older, newer) = Order(a, b);
        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant(
            $"**{older.Application}** — A: {older.StartedAt:MM-dd HH:mm} (core {Off(older.CoreOffsetMHz)}, mem {Off(older.MemOffsetMHz)}) vs B: {newer.StartedAt:MM-dd HH:mm} (core {Off(newer.CoreOffsetMHz)}, mem {Off(newer.MemOffsetMHz)})"));

        // The on-screen comparison warns when the two sessions are different
        // applications; the markdown — the form that actually gets pasted into
        // reports and issues — dropped it, shipping an incomparable FPS delta
        // with no caveat attached.
        if (!string.Equals(older.Application, newer.Application, StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine(FormattableString.Invariant(
                $"> ⚠ Different applications ({older.Application} vs {newer.Application}) — FPS deltas are not comparable."));
        }

        sb.AppendLine();
        sb.AppendLine("| Metric | A | B | Δ |");
        sb.AppendLine("|---|---|---|---|");
        Row(sb, "Avg FPS", older.AvgFps, newer.AvgFps, "F1");
        Row(sb, "1% low FPS", older.Low1Fps, newer.Low1Fps, "F1");
        Row(sb, "P1 FPS", older.P1Fps, newer.P1Fps, "F1");
        Row(sb, "Board power (W)", older.AvgPowerW, newer.AvgPowerW, "F0");
        Row(sb, "GPU temp (°C)", older.AvgGpuTempC, newer.AvgGpuTempC, "F1");
        Row(sb, "Mem junction (°C)", older.AvgMemJunctionC, newer.AvgMemJunctionC, "F1");
        return sb.ToString();
    }

    private static (SessionReport Older, SessionReport Newer) Order(SessionReport a, SessionReport b) =>
        a.StartedAt <= b.StartedAt ? (a, b) : (b, a);

    private static string Off(int? mhz) =>
        mhz is { } m ? m.ToString("+0;-0;0", CultureInfo.InvariantCulture) : "—";

    /// <summary>
    /// A metric that was never measured on one side has no delta. Reporting one
    /// anyway turned an absent sensor into a "0.0 °C → 0.0 °C (+0.0)" reading.
    /// </summary>
    private static string Delta(
        string label, double? a, double? b, string fmt, string unit = "", bool percentOfBase = false)
    {
        if (a is not { } x || b is not { } y)
        {
            return $"{label}: not measured";
        }

        double d = y - x;
        string line = string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1:" + fmt + "}{3} → {2:" + fmt + "}{3}  ({4}{5:" + fmt + "}{3}",
            label, x, y, unit, d >= 0 ? "+" : "", d);
        if (percentOfBase && x > 0)
        {
            line += string.Format(CultureInfo.InvariantCulture, ", {0}{1:F1}%", d >= 0 ? "+" : "", d / x * 100);
        }

        return line + ")";
    }

    private static void Row(StringBuilder sb, string label, double? a, double? b, string fmt)
    {
        // "—", never a fabricated 0, for a sensor that never reported. This
        // table is pasted into reports, where a printed 0 reads as a measurement.
        if (a is not { } x || b is not { } y)
        {
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "| {0} | {1} | {2} | — |",
                label,
                a is { } av ? av.ToString(fmt, CultureInfo.InvariantCulture) : "—",
                b is { } bv ? bv.ToString(fmt, CultureInfo.InvariantCulture) : "—"));
            return;
        }

        double d = y - x;
        sb.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "| {0} | {1:" + fmt + "} | {2:" + fmt + "} | {3}{4:" + fmt + "} |",
            label, x, y, d >= 0 ? "+" : "", d));
    }
}
