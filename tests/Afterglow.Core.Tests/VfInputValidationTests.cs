using Afterglow.Cli;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// Inputs that turn into V/F hardware writes are refused before anything is
/// planned or written when they are not finite (R-21), and every per-point
/// range check holds for every int a parser can produce (R-48).
/// </summary>
public class VfInputValidationTests
{
    // 700..1050 mV in 25 mV steps, 1300..2100 MHz (875 mV = 1700 MHz), as in VfPointFlattenTests.
    private static List<NvapiGpu.VfpTablePoint> Curve()
    {
        var points = new List<NvapiGpu.VfpTablePoint>();
        int index = 10;
        for (double mv = 700; mv <= 1050; mv += 25)
        {
            points.Add(new NvapiGpu.VfpTablePoint(index++, mv, Math.Round(1300 + ((mv - 700) / 350.0 * 800)), 0));
        }

        return points;
    }

    public static TheoryData<double> NonFinite => new() { double.NaN, double.PositiveInfinity, double.NegativeInfinity };

    [Theory]
    [MemberData(nameof(NonFinite))]
    public void A_non_finite_flatten_clock_is_refused_not_planned_as_all_zero(double clock)
    {
        // The finding's harness: a NaN target converted every delta to 0, so a
        // "valid" plan cleared the whole curve.
        var plan = VfPointPlanner.PlanFlatten(Curve(), 875, clock, out string? refusal);

        Assert.Null(plan);
        Assert.Contains("finite", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NonFinite))]
    public void A_non_finite_flatten_voltage_is_refused_not_anchored_at_the_lowest_point(double voltage)
    {
        // NaN or infinity is never nearer than points[0], so the flatten
        // anchored at 700 mV and wrote offsets nobody asked for.
        var plan = VfPointPlanner.PlanFlatten(Curve(), voltage, 1700, out string? refusal);

        Assert.Null(plan);
        Assert.Contains("finite", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finite_flatten_still_plans()
    {
        // Positive control for the two refusals above.
        var plan = VfPointPlanner.PlanFlatten(Curve(), 875, 1800, out string? refusal);

        Assert.Null(refusal);
        Assert.NotNull(plan);
        Assert.Equal(875, plan.AnchorVoltageMv);
        Assert.Equal(100, plan.OffsetsMHz[plan.AnchorIndex]);
    }

    [Fact]
    public void A_table_that_read_back_non_finite_is_not_planned_from()
    {
        var curve = Curve();
        curve[3] = curve[3] with { VoltageMv = double.NaN };

        Assert.Null(VfPointPlanner.PlanFlatten(curve, 875, 1800, out string? refusal));
        Assert.Contains("non-finite", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("875:NaN")]
    [InlineData("NaN:1875")]
    [InlineData("875:Infinity")]
    [InlineData("-Infinity:1875")]
    public void The_cli_refuses_a_non_finite_flatten_before_opening_the_gpu(string value)
    {
        // NumberStyles.Float parses these; Parse runs before the GPU stack opens.
        var request = VfPointsCommand.Parse(["vfpoints", "--flatten", value], out string? error);

        Assert.Null(request);
        Assert.Contains("finite", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cli_still_accepts_a_finite_flatten()
    {
        var request = VfPointsCommand.Parse(["vfpoints", "--flatten", "875:1875"], out string? error);

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal((875d, 1875d), (request.FlattenVoltageMv, request.FlattenClockMHz));
    }

    [Theory]
    [MemberData(nameof(NonFinite))]
    public void A_non_finite_undervolt_target_is_refused(double value)
    {
        var recorder = new VfCurveRecorder();
        recorder.SetTuningState(VfTuningState.Stock);
        for (int i = 0; i < 25; i++)
        {
            recorder.Add(Sample(900, 2800));
        }

        Assert.NotNull(recorder.PlanUndervolt(900, 2900, VfTuningState.Stock)); // the curve can plan
        Assert.Null(recorder.PlanUndervolt(value, 2900, VfTuningState.Stock));
        Assert.Null(recorder.PlanUndervolt(900, value, VfTuningState.Stock));
    }

    [Fact]
    public void A_non_finite_telemetry_voltage_is_not_binned()
    {
        // The range bounds are false for NaN, which then rounded to bin 0: a
        // "0 mV" point in a curve that drives writes.
        var recorder = new VfCurveRecorder();

        Assert.False(recorder.TryAdd(Sample(double.NaN, 2800), out _));
        Assert.False(recorder.TryAdd(Sample(double.PositiveInfinity, 2800), out _));
        Assert.Empty(recorder.GetCurve(minSamples: 1));
        Assert.True(recorder.TryAdd(Sample(900, 2800), out double bin));
        Assert.Equal(900, bin);
    }

    // --- R-48 ------------------------------------------------------------------

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(-NvapiGpu.VfpOffsetLimitMHz - 1)]
    [InlineData(NvapiGpu.VfpOffsetLimitMHz + 1)]
    public void An_out_of_range_point_offset_is_refused_for_every_int(int offset)
    {
        // Math.Abs(int.MinValue) threw OverflowException here instead of refusing.
        string? refusal = VfPointPlanner.ValidatePointOffsets(new Dictionary<int, int> { [30] = offset }, liveSlots: null);

        Assert.NotNull(refusal);
        Assert.Contains("outside", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-NvapiGpu.VfpOffsetLimitMHz)]
    [InlineData(0)]
    [InlineData(NvapiGpu.VfpOffsetLimitMHz)]
    public void The_range_edges_are_still_accepted(int offset) =>
        Assert.Null(VfPointPlanner.ValidatePointOffsets(new Dictionary<int, int> { [30] = offset }, liveSlots: null));

    [Theory]
    [InlineData("30=-2147483648")]
    [InlineData("30=2147483647")]
    public void The_cli_refuses_an_extreme_offset_as_a_usage_error(string set)
    {
        // The finding's command line: an unhandled OverflowException before the
        // GPU was opened, instead of a validation error and exit 2.
        var request = VfPointsCommand.Parse(["vfpoints", "--set", set], out string? error);

        Assert.Null(request);
        Assert.Contains("outside", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flatten_whose_delta_saturates_an_int_is_refused_not_thrown()
    {
        // A finite but absurd stored clock (what a corrupted read could hand
        // back) rounds the delta to int.MinValue; the per-point check above
        // the write must refuse it, not throw.
        var curve = Curve();
        curve[^1] = curve[^1] with { ClockMHz = 1e12 };

        var plan = VfPointPlanner.PlanFlatten(curve, 875, 1800, out string? refusal);

        Assert.Null(plan);
        Assert.Contains("outside", refusal, StringComparison.Ordinal);
    }

    private static GpuSnapshot Sample(double mv, uint mhz) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        DeviceIndex = 0,
        CoreVoltageMv = mv,
        CoreClockMHz = mhz,
        GpuUtilPct = 95,
    };
}
