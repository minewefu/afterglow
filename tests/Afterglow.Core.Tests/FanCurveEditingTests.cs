using Afterglow.Core.Fans;

namespace Afterglow.Core.Tests;

public class FanCurveEditingTests
{
    // The editor's chart spans 20–100 °C.
    private const double ChartMin = 20;
    private const double ChartMax = 100;

    private static FanPoint[] Curve(params double[] temps) =>
        [.. temps.Select((t, i) => new FanPoint(t, Math.Min(100, 30 + (i * 10))))];

    [Fact]
    public void Double_clicking_an_existing_point_adds_nothing()
    {
        // The review's trigger: a double-click on the 50 °C point inserted a
        // second 50 °C point, which Validate then refused (R-40).
        var points = FanCurveConfig.DefaultPoints;

        Assert.Null(FanCurveEditing.AddPoint(points, 50.2, 31));
        Assert.Null(FanCurveEditing.AddPoint(points, 49.6, 30));   // rounds onto 50 from below
    }

    [Fact]
    public void A_new_point_keeps_a_degree_from_each_neighbour()
    {
        // Between points 1 °C apart there is no whole degree left.
        Assert.Null(FanCurveEditing.AddPoint(Curve(40, 50, 51, 90), 50.5, 45));

        // A hand-edited curve may hold fractional temperatures: the new point is
        // placed by its rounded temperature, not by the raw click (50.4 used to
        // land after 50.3 as "50").
        var points = Curve(40, 50.3, 60, 90);
        Assert.Null(FanCurveEditing.AddPoint(points, 50.4, 45));

        var added = FanCurveEditing.AddPoint(points, 48.6, 45);
        Assert.NotNull(added);
        Assert.Equal([40, 49, 50.3, 60, 90], added.Select(p => p.TempC));
    }

    [Fact]
    public void An_accepted_point_lands_in_order()
    {
        var added = FanCurveEditing.AddPoint(FanCurveConfig.DefaultPoints, 70.4, 50.6);

        Assert.NotNull(added);
        Assert.Equal([30, 50, 65, 70, 75, 83, 90], added.Select(p => p.TempC));
        Assert.Equal(new FanPoint(70, 51), added[3]);
        Assert.Null(new FanCurveConfig { Points = added }.Validate());
    }

    [Fact]
    public void A_full_curve_takes_no_more_points()
    {
        var full = Enumerable.Range(0, FanCurveConfig.MaxPoints)
            .Select(i => new FanPoint(20 + (i * 5), 30 + (i * 4)))
            .ToArray();

        Assert.Null(FanCurveEditing.AddPoint(full, 22, 30));
    }

    [Fact]
    public void Dragging_a_point_with_no_room_between_its_neighbours_moves_only_its_duty()
    {
        // Neighbours less than 2 °C apart (a duplicate the editor used to create,
        // or a hand-edited file) leave no whole degree for the dragged point. The
        // clamp's lower bound then exceeded its upper bound, and Math.Clamp threw
        // on every mouse move (R-40).
        var moved = FanCurveEditing.MovePoint(Curve(30, 50, 50.5, 51, 90), 2, 70, 66, ChartMin, ChartMax);

        Assert.NotNull(moved);
        Assert.Equal(new FanPoint(50.5, 66), moved[2]);
        Assert.Equal(66, moved[3].DutyPct);   // the hotter neighbour is lifted, as for any drag
    }

    [Fact]
    public void Dragging_keeps_a_whole_degree_from_each_neighbour()
    {
        var points = Curve(30, 50, 65, 90);
        Assert.Equal(64, FanCurveEditing.MovePoint(points, 1, 80, 40, ChartMin, ChartMax)![1].TempC);
        Assert.Equal(31, FanCurveEditing.MovePoint(points, 1, 10, 40, ChartMin, ChartMax)![1].TempC);

        // Fractional neighbours: the nearest whole degree at least 1 °C away.
        points = Curve(30, 50.3, 55, 60.6, 90);
        Assert.Equal(52, FanCurveEditing.MovePoint(points, 2, 40, 50, ChartMin, ChartMax)![2].TempC);
        Assert.Equal(59, FanCurveEditing.MovePoint(points, 2, 80, 50, ChartMin, ChartMax)![2].TempC);

        // The end points stay on the chart.
        Assert.Equal(20, FanCurveEditing.MovePoint(points, 0, 5, 30, ChartMin, ChartMax)![0].TempC);
        Assert.Equal(100, FanCurveEditing.MovePoint(points, 4, 120, 100, ChartMin, ChartMax)![4].TempC);
    }

    [Fact]
    public void A_stale_drag_index_is_no_edit()
    {
        // The bound curve can be replaced under a drag (a profile applied by a
        // shortcut, say); the index the drag started with may no longer exist.
        Assert.Null(FanCurveEditing.MovePoint(Curve(30, 60, 90), 3, 50, 50, ChartMin, ChartMax));
        Assert.Null(FanCurveEditing.MovePoint(Curve(30, 60, 90), -1, 50, 50, ChartMin, ChartMax));
    }

    [Fact]
    public void Removing_keeps_at_least_two_points()
    {
        Assert.Null(FanCurveEditing.RemovePoint(Curve(30, 90), 0));
        Assert.Null(FanCurveEditing.RemovePoint(Curve(30, 60, 90), 3));
        Assert.Equal([30, 90], FanCurveEditing.RemovePoint(Curve(30, 60, 90), 1)!.Select(p => p.TempC));
    }

    [Fact]
    public void Any_sequence_of_edits_keeps_whole_ordered_temperatures_and_rising_duties()
    {
        // The editor's three gestures, driven with a fixed seed: thousands of
        // adds, drags and removals anywhere on the chart, a third of them aimed
        // within half a degree of an existing point as a double-click on one is.
        var random = new Random(4021);
        IReadOnlyList<FanPoint> points = FanCurveConfig.DefaultPoints;
        for (int step = 0; step < 5000; step++)
        {
            int index = random.Next(points.Count);
            double temp = random.Next(3) == 0
                ? points[index].TempC + random.NextDouble() - 0.5
                : ChartMin + (random.NextDouble() * (ChartMax - ChartMin));
            temp = Math.Clamp(temp, ChartMin, ChartMax);   // as the editor's ToData does
            double duty = random.NextDouble() * 100;

            points = random.Next(3) switch
            {
                0 => FanCurveEditing.AddPoint(points, temp, duty),
                1 => FanCurveEditing.MovePoint(points, index, temp, duty, ChartMin, ChartMax),
                _ => FanCurveEditing.RemovePoint(points, index),
            } ?? points;

            string context = $"step {step}: {string.Join(", ", points.Select(p => $"{p.TempC}/{p.DutyPct}"))}";
            Assert.InRange(points.Count, 2, FanCurveConfig.MaxPoints);
            for (int i = 0; i < points.Count; i++)
            {
                Assert.True(points[i].TempC == Math.Round(points[i].TempC), context);
                Assert.True(points[i].TempC is >= ChartMin and <= ChartMax, context);
                Assert.True(points[i].DutyPct is >= 0 and <= 100, context);
                if (i > 0)
                {
                    Assert.True(points[i].TempC - points[i - 1].TempC >= 1, context);
                    Assert.True(points[i].DutyPct >= points[i - 1].DutyPct, context);
                }
            }

            // The only thing Validate may still refuse is a curve the user dragged
            // below the safety floor at its hottest point — a message, not a bug.
            Assert.True(
                new FanCurveConfig { Points = points }.Validate() is null ||
                points[^1].DutyPct < FanCurveConfig.MinTopDutyPct,
                context);
        }
    }
}
