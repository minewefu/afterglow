using System.Runtime.InteropServices;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Profiles;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

public class VfPointPlannerTests
{
    private static List<NvapiGpu.VfpTablePoint> Curve()
    {
        // A plausible Ampere-ish rising curve: 700..1050 mV, 1300..2100 MHz.
        var points = new List<NvapiGpu.VfpTablePoint>();
        int index = 10;
        for (double mv = 700; mv <= 1050; mv += 25)
        {
            double mhz = 1300 + ((mv - 700) / 350.0 * 800);
            points.Add(new NvapiGpu.VfpTablePoint(index++, mv, Math.Round(mhz), 0));
        }

        return points;
    }

    [Fact]
    public void Flatten_raises_the_anchor_and_caps_every_higher_point()
    {
        var curve = Curve();
        var plan = VfPointPlanner.PlanFlatten(curve, 875, 1875, out string? refusal);

        Assert.Null(refusal);
        Assert.NotNull(plan);
        Assert.Equal(875, plan.AnchorVoltageMv);

        var anchor = curve.Single(p => p.VoltageMv == 875);
        Assert.Equal((int)Math.Round(1875 - anchor.ClockMHz), plan.OffsetsMHz[anchor.Index]);

        foreach (var point in curve)
        {
            if (point.VoltageMv > 875)
            {
                // Capped: stored + offset == target.
                Assert.Equal(1875, point.ClockMHz + plan.OffsetsMHz[point.Index]);
            }
            else if (point.VoltageMv < 875)
            {
                // Deterministic write: explicitly stock below the anchor.
                Assert.Equal(0, plan.OffsetsMHz[point.Index]);
            }
        }

        Assert.Equal(curve.Count(p => p.VoltageMv > 875), plan.PointsFlattened);
    }

    [Fact]
    public void Flatten_snaps_to_the_nearest_stored_voltage()
    {
        var plan = VfPointPlanner.PlanFlatten(Curve(), 881, 1875, out _);

        Assert.NotNull(plan);
        Assert.Equal(875, plan.AnchorVoltageMv);
    }

    [Fact]
    public void Absurd_targets_are_refused_with_a_reason()
    {
        Assert.Null(VfPointPlanner.PlanFlatten(Curve(), 875, 3500, out string? refusal));
        Assert.NotNull(refusal);
        Assert.Contains("MHz", refusal, StringComparison.Ordinal);

        Assert.Null(VfPointPlanner.PlanFlatten([], 875, 1875, out string? empty));
        Assert.NotNull(empty);
    }

    [Fact]
    public void Profile_validation_bounds_per_point_offsets()
    {
        var bad = new TuningProfile
        {
            Name = "vfp",
            VfPointOffsetsMHz = new Dictionary<int, int> { [300] = 50 },
        };
        Assert.Contains("index", bad.Validate(), StringComparison.OrdinalIgnoreCase);

        var absurd = new TuningProfile
        {
            Name = "vfp",
            VfPointOffsetsMHz = new Dictionary<int, int> { [10] = 2000 },
        };
        Assert.Contains("1500", absurd.Validate(), StringComparison.Ordinal);

        var fine = new TuningProfile
        {
            Name = "vfp",
            VfPointOffsetsMHz = new Dictionary<int, int> { [10] = -120, [200] = 90 },
        };
        Assert.Null(fine.Validate());
    }
}

public class VfPointStructLayoutTests
{
    [Fact]
    public void Clock_boost_structs_match_the_driver_wire_sizes()
    {
        // Sizes from the field-proven nvapioc layouts: masks 6188, curve 7208,
        // table 9248 (68-byte header + 255 × 24/28/36-byte entries). A drift
        // here means the version stamp is wrong and every call would fail
        // with IncompatibleStructVersion.
        Assert.Equal(6188, Marshal.SizeOf<Afterglow.Core.Interop.Nvapi.NvClockMasks>());
        Assert.Equal(7208, Marshal.SizeOf<Afterglow.Core.Interop.Nvapi.NvVfpCurve>());
        Assert.Equal(9248, Marshal.SizeOf<Afterglow.Core.Interop.Nvapi.NvClockTable>());
    }
}

/// <summary>
/// The two pure decisions that gate removing a user's curve: is there per-point
/// shape in the table at all, and did the profile ever look at the table?
/// </summary>
public class VfPointReconcileTests
{
    private static NvapiGpu.VfpTablePoint P(int i, int offset) => new(i, 800 + i, 1500 + i, offset);

    [Fact]
    public void A_uniform_table_is_not_shaped_however_large_the_offset()
    {
        // What a global core offset alone looks like: same delta on every slot.
        Assert.False(VfPointPlanner.HasPerPointShape([P(0, 100), P(1, 100), P(2, 100)]));
        Assert.False(VfPointPlanner.HasPerPointShape([P(0, 0), P(1, 0)]));
        Assert.False(VfPointPlanner.HasPerPointShape([P(0, 250)]));
        Assert.False(VfPointPlanner.HasPerPointShape([]));
    }

    [Fact]
    public void One_slot_out_of_line_is_shape()
    {
        Assert.True(VfPointPlanner.HasPerPointShape([P(0, 100), P(1, 100), P(2, -50)]));
        Assert.True(VfPointPlanner.HasPerPointShape([P(0, -50), P(1, 100)]));
    }

    [Fact]
    public void Only_a_profile_that_read_the_table_may_claim_it_carries_no_offsets()
    {
        // A profile assembled from ReadCurrent (CLI --core-offset, MCP tuning,
        // the stepper, the post-game restore) cannot see point offsets, so its
        // silence must never be read as "remove the curve".
        var fromReadCurrent = new TuningProfile { Name = "partial set", CoreOffsetMHz = 100 };
        Assert.False(fromReadCurrent.CapturedVfPoints);
        Assert.Null(fromReadCurrent.VfPointOffsetsMHz);

        // A profile saved after reading the table says the same thing and means it.
        var saved = fromReadCurrent with { CapturedVfPoints = true };
        Assert.True(saved.CapturedVfPoints);
        Assert.Null(saved.Validate());
    }
}
