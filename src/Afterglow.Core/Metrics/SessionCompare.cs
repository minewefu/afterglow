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
        if (older.AvgMemJunctionC > 0 && newer.AvgMemJunctionC > 0)
        {
            sb.AppendLine(Delta("Mem junction", older.AvgMemJunctionC, newer.AvgMemJunctionC, "F1", unit: " °C"));
        }

        if (older.AvgPowerW > 1 && newer.AvgPowerW > 1)
        {
            sb.AppendLine(Delta(
                "FPS per watt", older.AvgFps / older.AvgPowerW, newer.AvgFps / newer.AvgPowerW,
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

    private static string Off(int mhz) => mhz.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    private static string Delta(
        string label, double a, double b, string fmt, string unit = "", bool percentOfBase = false)
    {
        double d = b - a;
        string line = string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1:" + fmt + "}{3} → {2:" + fmt + "}{3}  ({4}{5:" + fmt + "}{3}",
            label, a, b, unit, d >= 0 ? "+" : "", d);
        if (percentOfBase && a > 0)
        {
            line += string.Format(CultureInfo.InvariantCulture, ", {0}{1:F1}%", d >= 0 ? "+" : "", d / a * 100);
        }

        return line + ")";
    }

    private static void Row(StringBuilder sb, string label, double a, double b, string fmt)
    {
        double d = b - a;
        sb.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "| {0} | {1:" + fmt + "} | {2:" + fmt + "} | {3}{4:" + fmt + "} |",
            label, a, b, d >= 0 ? "+" : "", d));
    }
}
