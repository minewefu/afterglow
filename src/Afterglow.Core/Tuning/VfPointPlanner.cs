using Afterglow.Core.Interop.Nvapi;

namespace Afterglow.Core.Tuning;

/// <summary>
/// Pure math for per-point curve edits. The classic flatten undervolt (what
/// Afterburner users do by hand): raise the point at the target voltage to the
/// target clock, cap every higher-voltage point to the same clock, and zero
/// everything below so the write is deterministic — the GPU then runs the
/// target clock at the target voltage and never boosts past it into higher
/// voltage bins.
/// </summary>
public static class VfPointPlanner
{
    /// <summary>Largest per-point delta the planner will ask for (driver limit is wider).</summary>
    public const int MaxPlannedOffsetMHz = 1000;

    public sealed record FlattenPlan(
        IReadOnlyDictionary<int, int> OffsetsMHz,
        int AnchorIndex,
        double AnchorVoltageMv,
        double AnchorStoredClockMHz,
        double TargetClockMHz,
        int PointsFlattened);

    /// <summary>
    /// Builds the full-table offset set for a flatten undervolt, or null with a
    /// reason when the request is not sane against the stored curve.
    /// </summary>
    public static FlattenPlan? PlanFlatten(
        IReadOnlyList<NvapiGpu.VfpTablePoint> points,
        double targetVoltageMv,
        double targetClockMHz,
        out string? refusal)
    {
        refusal = null;
        if (points.Count < 2)
        {
            refusal = "The driver exposed no per-point curve to edit.";
            return null;
        }

        if (targetClockMHz is < 300 or > 4500)
        {
            refusal = "Target clock outside 300..4500 MHz.";
            return null;
        }

        // Anchor: the stored point nearest the requested voltage.
        var anchor = points[0];
        foreach (var point in points)
        {
            if (Math.Abs(point.VoltageMv - targetVoltageMv) < Math.Abs(anchor.VoltageMv - targetVoltageMv))
            {
                anchor = point;
            }
        }

        int anchorDelta = (int)Math.Round(targetClockMHz - anchor.ClockMHz);
        if (Math.Abs(anchorDelta) > MaxPlannedOffsetMHz)
        {
            refusal = $"Reaching {targetClockMHz:F0} MHz at {anchor.VoltageMv:F0} mV needs a " +
                      $"{anchorDelta:+0;-0} MHz point offset — outside the ±{MaxPlannedOffsetMHz} MHz the " +
                      "planner considers sane. Pick a target closer to the stored curve.";
            return null;
        }

        var offsets = new Dictionary<int, int>();
        int flattened = 0;
        foreach (var point in points)
        {
            if (point.VoltageMv < anchor.VoltageMv)
            {
                // Deterministic write: lower points explicitly return to stock.
                offsets[point.Index] = 0;
                continue;
            }

            int delta = point.Index == anchor.Index
                ? anchorDelta
                : (int)Math.Round(targetClockMHz - point.ClockMHz);
            if (Math.Abs(delta) > NvapiGpu.VfpOffsetLimitMHz)
            {
                refusal = $"Capping the {point.VoltageMv:F0} mV point to {targetClockMHz:F0} MHz needs " +
                          $"{delta:+0;-0} MHz — outside the driver's ±{NvapiGpu.VfpOffsetLimitMHz} MHz range.";
                return null;
            }

            offsets[point.Index] = delta;
            if (point.Index != anchor.Index)
            {
                flattened++;
            }
        }

        return new FlattenPlan(
            offsets, anchor.Index, anchor.VoltageMv, anchor.ClockMHz, targetClockMHz, flattened);
    }
}
