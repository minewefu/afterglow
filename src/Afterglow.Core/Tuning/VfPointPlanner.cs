using Afterglow.Core.Interop.Nvapi;

namespace Afterglow.Core.Tuning;

/// <summary>
/// Pure math for per-point curve edits. The classic flatten undervolt (what
/// Afterburner users do by hand): raise the point at the target voltage to the
/// target clock, cap every higher-voltage point to the same clock, and write
/// every lower point explicitly so the result is deterministic — back to stock,
/// or down to the target where stock already runs faster than it. The GPU then
/// runs the target clock at the target voltage and no point anywhere on the
/// curve asks for more than the target.
/// </summary>
public static class VfPointPlanner
{
    /// <summary>Largest per-point delta the planner will ask for (driver limit is wider).</summary>
    public const int MaxPlannedOffsetMHz = 1000;

    /// <summary>Highest raw table slot (the clock-boost tables carry 255 slots, 0..254).</summary>
    public const int MaxSlotIndex = 254;

    /// <summary>
    /// True when the table carries per-point SHAPE — at least two live slots
    /// with different deltas. A global core offset alone reads the same delta on
    /// every slot (measured on RTX 5090 / driver 616.56); the converse does not
    /// follow, since a per-point edit that happens to be uniform reads the same
    /// way. This detects a shaped curve, not every possible edit.
    /// </summary>
    public static bool HasPerPointShape(IReadOnlyList<NvapiGpu.VfpTablePoint> points)
    {
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].OffsetMHz != points[0].OffsetMHz)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What a saved profile records of the table (F-02/F-07). Applying a
    /// profile writes the core offset — which levels every slot to it on the
    /// hardware this was measured on — and then only the slots the profile
    /// carries; any slot it leaves out keeps whatever is in the table at that
    /// moment. The old capture kept only non-zero slots, so the explicit zeros
    /// a flatten writes below its anchor were dropped and came back as the
    /// profile's core offset (or as a stale manual edit) on re-apply, under a
    /// "verified" label because only the carried slots are re-checked.
    ///
    /// So: when every live slot reads exactly the global core offset, the
    /// table holds nothing the core offset alone won't reproduce and no points
    /// are recorded (null — the profile still records that it read the table).
    /// Otherwise EVERY live slot is recorded, zeros included, so re-apply
    /// replaces the whole curve instead of merging into whatever is there.
    /// </summary>
    /// <param name="table">The live table as read back (offsets are what each slot holds).</param>
    /// <param name="globalCoreOffsetMHz">The core offset the driver reports right now.</param>
    public static IReadOnlyDictionary<int, int>? CaptureForProfile(
        IReadOnlyList<NvapiGpu.VfpTablePoint> table, int globalCoreOffsetMHz)
    {
        if (table.All(point => point.OffsetMHz == globalCoreOffsetMHz))
        {
            return null;
        }

        var captured = new Dictionary<int, int>(table.Count);
        foreach (var point in table)
        {
            captured[point.Index] = point.OffsetMHz;
        }

        return captured;
    }

    /// <summary>
    /// Refuses a per-point write before anything is written, or returns null
    /// when it may go ahead. Out-of-range offsets used to be clamped silently by
    /// the interop and then compared against the same clamp, so "30=9000" wrote
    /// +1500 and reported "verified" (F-10/F-58). The same ±limit a saved
    /// profile is validated against now applies at every entry point, and the
    /// request is refused rather than reduced.
    /// </summary>
    /// <param name="liveSlots">
    /// The live core slots read from the table, or null to check only what can
    /// be checked without hardware (the CLI does that before opening the GPU).
    /// A slot outside this set would be skipped by the driver write and then
    /// fail readback — refusing it up front keeps the write all-or-nothing.
    /// </param>
    public static string? ValidatePointOffsets(
        IReadOnlyDictionary<int, int> offsetsMHzByIndex, IReadOnlySet<int>? liveSlots)
    {
        if (offsetsMHzByIndex.Count == 0)
        {
            return "No point offsets were given — nothing was written.";
        }

        foreach (var (index, offset) in offsetsMHzByIndex.OrderBy(pair => pair.Key))
        {
            if (index is < 0 or > MaxSlotIndex)
            {
                return FormattableString.Invariant(
                    $"Slot {index} is outside the table (0..{MaxSlotIndex}) — nothing was written.");
            }

            if (liveSlots is not null && !liveSlots.Contains(index))
            {
                return FormattableString.Invariant(
                    $"Slot {index} is not a live core V/F point on this GPU (list them with `afterglow-cli vfpoints`) — nothing was written.");
            }

            // Relational, never Math.Abs: |int.MinValue| does not fit an int,
            // so "30=-2147483648" threw OverflowException here instead of
            // being refused (R-48).
            if (offset is < -NvapiGpu.VfpOffsetLimitMHz or > NvapiGpu.VfpOffsetLimitMHz)
            {
                return FormattableString.Invariant(
                    $"Slot {index}: {offset:+0;-0} MHz is outside the driver's ±{NvapiGpu.VfpOffsetLimitMHz} MHz per-point range — nothing was written.");
            }
        }

        return null;
    }

    /// <summary>
    /// Verifies a per-point write against what was REQUESTED (F-58: the old
    /// check compared against the request after the same clamp the write
    /// applied, so a silently reduced write always matched). Requests reaching
    /// here have passed <see cref="ValidatePointOffsets"/>, so nothing was
    /// clamped and the only way to match is for the driver to hold exactly the
    /// asked-for value.
    /// </summary>
    public static KnobResult VerifyPointWrite(
        IReadOnlyDictionary<int, int> requested, IReadOnlyList<NvapiGpu.VfpTablePoint> readback)
    {
        const string knob = "V/F points";
        var byIndex = new Dictionary<int, int>();
        foreach (var point in readback)
        {
            byIndex[point.Index] = point.OffsetMHz;
        }

        int matched = 0;
        string? firstMismatch = null;
        foreach (var (index, offset) in requested.OrderBy(pair => pair.Key))
        {
            if (byIndex.TryGetValue(index, out int actual) && actual == offset)
            {
                matched++;
                continue;
            }

            firstMismatch ??= byIndex.TryGetValue(index, out actual)
                ? FormattableString.Invariant($"slot {index}: asked {offset:+0;-0;0} MHz, reads {actual:+0;-0;0} MHz")
                : FormattableString.Invariant($"slot {index}: missing from the readback");
        }

        return matched == requested.Count
            ? KnobResult.Ok(knob, FormattableString.Invariant($"{matched} point offsets (verified)"))
            : KnobResult.Fail(knob, FormattableString.Invariant(
                $"driver accepted the write but readback matched only {matched}/{requested.Count} points ({firstMismatch})"));
    }

    /// <summary>
    /// Judges a clear-all from the table read back after it. The clear used to
    /// report "ok — cleared" from the driver's return code alone (F-06, F-43,
    /// F-57), so a driver that accepted the write and kept the offsets would
    /// have been reported as cleared — the exact failure the per-point readback
    /// exists to catch.
    ///
    /// What "cleared" means is measured, not assumed (RTX 5090, driver 616.56):
    /// the table read back uniform at the global core offset (+100 MHz in that
    /// test), i.e. the clear removed per-point shape and left the global offset
    /// in place. So success is "no per-point shape left"; the uniform value is
    /// reported, and if it disagrees with the core offset the driver reports,
    /// that is said too rather than guessed at — other drivers may differ.
    /// </summary>
    /// <param name="readStatus">Status of the readback after the clear.</param>
    /// <param name="after">The table read back after the clear.</param>
    /// <param name="globalCoreOffsetMHz">The core offset the driver reports now, when it would say.</param>
    public static KnobResult VerifyClear(
        NvapiStatus readStatus, IReadOnlyList<NvapiGpu.VfpTablePoint> after, int? globalCoreOffsetMHz)
    {
        const string knob = "V/F points";
        if (readStatus != NvapiStatus.Ok)
        {
            return KnobResult.Ok(knob, $"cleared by the driver — not verified (the table did not read back: {readStatus})");
        }

        if (after.Count == 0)
        {
            return KnobResult.Ok(knob, "cleared by the driver — not verified (the table read back with no live points)");
        }

        if (HasPerPointShape(after))
        {
            // Name the dominant value and how many points stand out from it.
            int common = after.GroupBy(p => p.OffsetMHz).OrderByDescending(g => g.Count()).First().Key;
            int differing = after.Count(p => p.OffsetMHz != common);
            return KnobResult.Fail(knob, FormattableString.Invariant(
                $"the driver accepted the clear, but per-point offsets are still in force: {differing} of {after.Count} points differ from the {common:+0;-0;0} MHz the rest read"));
        }

        int uniform = after[0].OffsetMHz;
        if (globalCoreOffsetMHz is not int global)
        {
            return KnobResult.Ok(knob, FormattableString.Invariant(
                $"cleared (verified: no per-point offsets left; every point reads {uniform:+0;-0;0} MHz)"));
        }

        if (uniform == global)
        {
            return KnobResult.Ok(knob, uniform == 0
                ? "cleared (verified: every point reads 0 MHz)"
                : FormattableString.Invariant(
                    $"cleared (verified: no per-point offsets left; every point reads the {uniform:+0;-0} MHz global core offset, which the clear left in place)"));
        }

        // Shape is gone, which is what was asked; the global offset no longer
        // lines up with the table. Not a failed clear — but not something to
        // stay quiet about either, since it changes what the card runs.
        return KnobResult.Ok(knob, FormattableString.Invariant(
            $"cleared (verified: no per-point offsets left), but every point reads {uniform:+0;-0;0} MHz while the driver reports a {global:+0;-0;0} MHz core offset — check the core offset (Tuning page, or `afterglow-cli get`)"));
    }

    /// <param name="PointsFlattened">Higher-voltage points capped to the target.</param>
    /// <param name="LowerPointsCapped">
    /// Lower-voltage points whose stock clock already exceeded the target and
    /// were brought down to it (a target below the stored curve); zero for the
    /// usual upward flatten, where every lower point simply returns to stock.
    /// </param>
    public sealed record FlattenPlan(
        IReadOnlyDictionary<int, int> OffsetsMHz,
        int AnchorIndex,
        double AnchorVoltageMv,
        double AnchorStoredClockMHz,
        double TargetClockMHz,
        int PointsFlattened,
        int LowerPointsCapped = 0);

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

        // Finite first (R-21): every relational bound below is false for NaN,
        // so a NaN clock passed the range check, every computed delta then
        // converted to 0 and the "plan" cleared the whole curve; a NaN or
        // infinite voltage is never nearer than the first point, so the
        // flatten anchored at the lowest-voltage slot nobody asked for.
        if (!double.IsFinite(targetVoltageMv) || !double.IsFinite(targetClockMHz))
        {
            refusal = "The target voltage and clock must be finite numbers (e.g. 875 mV and 1875 MHz).";
            return null;
        }

        if (targetClockMHz is < 300 or > 4500)
        {
            refusal = "Target clock outside 300..4500 MHz.";
            return null;
        }

        // The same reasoning for what the driver handed back: a non-finite
        // stored voltage or clock would corrupt the anchor search and every
        // delta computed from it.
        if (points.Any(point => !double.IsFinite(point.VoltageMv) || !double.IsFinite(point.ClockMHz)))
        {
            refusal = "The stored V/F table read back with a non-finite voltage or clock — nothing was planned.";
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
        if (anchorDelta is < -MaxPlannedOffsetMHz or > MaxPlannedOffsetMHz) // relational: safe for every int (R-48)
        {
            refusal = FormattableString.Invariant(
                $"Reaching {targetClockMHz:F0} MHz at {anchor.VoltageMv:F0} mV needs a ") +
                FormattableString.Invariant(
                    $"{anchorDelta:+0;-0} MHz point offset — outside the ±{MaxPlannedOffsetMHz} MHz the ") +
                "planner considers sane. Pick a target closer to the stored curve.";
            return null;
        }

        var offsets = new Dictionary<int, int>();
        int flattened = 0;
        int lowerCapped = 0;
        foreach (var point in points)
        {
            int delta;
            if (point.Index == anchor.Index)
            {
                delta = anchorDelta;
            }
            else if (point.VoltageMv < anchor.VoltageMv)
            {
                // Deterministic write: lower points explicitly return to stock —
                // unless stock already runs faster than the target, which is what
                // a target below the stored curve means. Zeroing those (F-11)
                // left e.g. 1643 MHz at 850 mV under a "flatten at 875 mV to
                // 1500 MHz", a higher clock at a lower voltage than the cap the
                // user just pinned. They are capped to the target like the rest.
                delta = Math.Min(0, (int)Math.Round(targetClockMHz - point.ClockMHz));
                if (delta < 0)
                {
                    lowerCapped++;
                }
            }
            else
            {
                delta = (int)Math.Round(targetClockMHz - point.ClockMHz);
                flattened++;
            }

            if (delta is < -NvapiGpu.VfpOffsetLimitMHz or > NvapiGpu.VfpOffsetLimitMHz)
            {
                refusal = FormattableString.Invariant(
                    $"Capping the {point.VoltageMv:F0} mV point to {targetClockMHz:F0} MHz needs ") +
                    FormattableString.Invariant(
                        $"{delta:+0;-0} MHz — outside the driver's ±{NvapiGpu.VfpOffsetLimitMHz} MHz range.");
                return null;
            }

            offsets[point.Index] = delta;
        }

        return new FlattenPlan(
            offsets, anchor.Index, anchor.VoltageMv, anchor.ClockMHz, targetClockMHz, flattened, lowerCapped);
    }
}
