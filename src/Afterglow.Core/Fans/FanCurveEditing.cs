namespace Afterglow.Core.Fans;

/// <summary>
/// The fan-curve editor's three gestures — double-click to add a point, drag to
/// move one, right-click to remove one — kept out of the WPF control so their
/// guarantees are testable. Every point the editor places sits on a whole
/// degree at least <see cref="MinSpacingC"/> from its neighbours, temperatures
/// stay strictly increasing and duties never decrease. A gesture that can't
/// keep that returns null: no edit. The editor used to insert a point wherever
/// the click rounded to, including onto an existing one, and the duplicate made
/// a later drag ask Math.Clamp for a lower bound above its upper bound, which
/// threw on every mouse move (R-40).
/// </summary>
public static class FanCurveEditing
{
    /// <summary>Smallest gap, in °C, the editor leaves between neighbouring points.</summary>
    public const double MinSpacingC = 1;

    /// <summary>
    /// Adds a point at the rounded temperature and duty, or returns null when the
    /// curve is full or the rounded temperature is closer than
    /// <see cref="MinSpacingC"/> to an existing point (a double-click on a point,
    /// or between two points one degree apart).
    /// </summary>
    public static IReadOnlyList<FanPoint>? AddPoint(IReadOnlyList<FanPoint> points, double tempC, double dutyPct)
    {
        if (points.Count >= FanCurveConfig.MaxPoints || !double.IsFinite(tempC) || !double.IsFinite(dutyPct))
        {
            return null;
        }

        // Placed by the rounded temperature it will have, not the raw click: a
        // click at 50.4 is a point at 50, which belongs before a point at 50.3.
        double temp = Math.Round(tempC);
        var list = points.ToList();
        int insertAt = list.FindIndex(p => p.TempC > temp);
        if (insertAt < 0)
        {
            insertAt = list.Count;
        }

        if ((insertAt > 0 && temp - list[insertAt - 1].TempC < MinSpacingC) ||
            (insertAt < list.Count && list[insertAt].TempC - temp < MinSpacingC))
        {
            return null;
        }

        list.Insert(insertAt, new FanPoint(temp, Math.Round(Math.Clamp(dutyPct, 0, 100))));
        return MakeMonotonic(list, insertAt);
    }

    /// <summary>
    /// Moves point <paramref name="index"/> toward the given temperature and duty:
    /// the temperature to the nearest whole degree at least
    /// <see cref="MinSpacingC"/> from each neighbour (the end points within
    /// <paramref name="minTempC"/>–<paramref name="maxTempC"/>), the duty anywhere,
    /// pushing neighbours' duties so the curve never decreases. Neighbours too
    /// close for any whole degree between them (as a hand-edited file may hold)
    /// leave the temperature where it is. Null for an index the curve no longer
    /// has.
    /// </summary>
    public static IReadOnlyList<FanPoint>? MovePoint(
        IReadOnlyList<FanPoint> points, int index, double tempC, double dutyPct, double minTempC, double maxTempC)
    {
        if (index < 0 || index >= points.Count || !double.IsFinite(tempC) || !double.IsFinite(dutyPct))
        {
            return null;
        }

        double lowest = Math.Ceiling(index > 0 ? points[index - 1].TempC + MinSpacingC : minTempC);
        double highest = Math.Floor(index < points.Count - 1 ? points[index + 1].TempC - MinSpacingC : maxTempC);
        double temp = lowest <= highest
            ? Math.Clamp(Math.Round(tempC), lowest, highest)
            : points[index].TempC;

        var list = points.ToList();
        list[index] = new FanPoint(temp, Math.Round(Math.Clamp(dutyPct, 0, 100)));
        return MakeMonotonic(list, index);
    }

    /// <summary>Removes point <paramref name="index"/>, or returns null when that would leave fewer than two.</summary>
    public static IReadOnlyList<FanPoint>? RemovePoint(IReadOnlyList<FanPoint> points, int index)
    {
        if (index < 0 || index >= points.Count || points.Count <= 2)
        {
            return null;
        }

        var list = points.ToList();
        list.RemoveAt(index);
        return list;
    }

    /// <summary>Pushes neighbouring duties so the curve never decreases, anchored at the edited index.</summary>
    private static List<FanPoint> MakeMonotonic(List<FanPoint> points, int anchor)
    {
        for (int i = anchor + 1; i < points.Count; i++)
        {
            if (points[i].DutyPct < points[i - 1].DutyPct)
            {
                points[i] = points[i] with { DutyPct = points[i - 1].DutyPct };
            }
        }

        for (int i = anchor - 1; i >= 0; i--)
        {
            if (points[i].DutyPct > points[i + 1].DutyPct)
            {
                points[i] = points[i] with { DutyPct = points[i + 1].DutyPct };
            }
        }

        return points;
    }
}
