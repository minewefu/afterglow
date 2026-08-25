using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Afterglow.Core.Fans;

namespace Afterglow.App.Controls;

/// <summary>
/// Interactive fan-curve editor: drag points, double-click to add, right-click
/// to remove. Shows the zero-RPM zone and a live temperature/duty marker.
/// Axis space: 20–100 °C × 0–100 %.
/// </summary>
public sealed class FanCurveEditor : FrameworkElement
{
    private const double TempMin = 20;
    private const double TempMax = 100;
    private const double HitRadius = 12;

    public static readonly DependencyProperty ConfigProperty = DependencyProperty.Register(
        nameof(Config), typeof(FanCurveConfig), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(new FanCurveConfig(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LiveTempProperty = DependencyProperty.Register(
        nameof(LiveTemp), typeof(double), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LiveDutyProperty = DependencyProperty.Register(
        nameof(LiveDuty), typeof(double), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(false));

    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)));
    private static readonly Brush ZeroRpmBrush = Freeze(new SolidColorBrush(Color.FromArgb(26, 77, 208, 225)));
    private static readonly Brush CurveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4D, 0xD0, 0xE1)));
    private static readonly Brush PointFill = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF4)));
    private static readonly Brush LiveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3C)));
    private static readonly Typeface LabelFace = new("Segoe UI");

    private int _dragIndex = -1;

    public FanCurveConfig Config
    {
        get => (FanCurveConfig)GetValue(ConfigProperty);
        set => SetValue(ConfigProperty, value);
    }

    public double LiveTemp
    {
        get => (double)GetValue(LiveTempProperty);
        set => SetValue(LiveTempProperty, value);
    }

    public double LiveDuty
    {
        get => (double)GetValue(LiveDutyProperty);
        set => SetValue(LiveDutyProperty, value);
    }

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>Raised after any user edit, with the updated configuration.</summary>
    public event EventHandler<FanCurveConfig>? CurveEdited;

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private Rect PlotRect => new(38, 8, Math.Max(10, ActualWidth - 50), Math.Max(10, ActualHeight - 34));

    private Point ToScreen(double tempC, double dutyPct)
    {
        var r = PlotRect;
        double x = r.X + ((tempC - TempMin) / (TempMax - TempMin) * r.Width);
        double y = r.Y + ((100 - dutyPct) / 100.0 * r.Height);
        return new Point(x, y);
    }

    private (double TempC, double DutyPct) ToData(Point p)
    {
        var r = PlotRect;
        double temp = TempMin + ((p.X - r.X) / r.Width * (TempMax - TempMin));
        double duty = 100 - ((p.Y - r.Y) / r.Height * 100);
        return (Math.Clamp(temp, TempMin, TempMax), Math.Clamp(duty, 0, 100));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawingContext dc = drawingContext;
        var r = PlotRect;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (r.Width < 20 || r.Height < 20)
        {
            return;
        }

        var config = Config;
        var gridPen = new Pen(GridBrush, 1);

        // Zero-RPM zone
        if (config.ZeroRpmBelowC > TempMin)
        {
            double zeroX = ToScreen(Math.Min(config.ZeroRpmBelowC, TempMax), 0).X;
            dc.DrawRectangle(ZeroRpmBrush, null, new Rect(r.X, r.Y, Math.Max(0, zeroX - r.X), r.Height));
            DrawLabel(dc, "zero-RPM", new Point(r.X + 6, r.Y + 4), 10);
        }

        // Grid + labels
        for (int temp = (int)TempMin; temp <= TempMax; temp += 10)
        {
            var p = ToScreen(temp, 0);
            dc.DrawLine(gridPen, new Point(p.X, r.Y), new Point(p.X, r.Bottom));
            DrawLabel(dc, $"{temp}°", new Point(p.X - 8, r.Bottom + 4), 10);
        }

        for (int duty = 0; duty <= 100; duty += 25)
        {
            var p = ToScreen(TempMin, duty);
            dc.DrawLine(gridPen, new Point(r.X, p.Y), new Point(r.Right, p.Y));
            DrawLabel(dc, $"{duty}%", new Point(4, p.Y - 7), 10);
        }

        // Curve
        var points = config.Points;
        var curvePen = new Pen(CurveBrush, 2) { LineJoin = PenLineJoin.Round };
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(ToScreen(TempMin, points[0].DutyPct), false, false);
            foreach (var point in points)
            {
                ctx.LineTo(ToScreen(point.TempC, point.DutyPct), true, false);
            }

            ctx.LineTo(ToScreen(TempMax, points[^1].DutyPct), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, curvePen, geometry);

        // Points
        for (int i = 0; i < points.Count; i++)
        {
            var p = ToScreen(points[i].TempC, points[i].DutyPct);
            dc.DrawEllipse(PointFill, new Pen(CurveBrush, 2), p, i == _dragIndex ? 7 : 5, i == _dragIndex ? 7 : 5);
            if (i == _dragIndex)
            {
                DrawLabel(dc, $"{points[i].TempC:F0}° → {points[i].DutyPct:F0}%",
                    new Point(p.X + 10, p.Y - 18), 11);
            }
        }

        // Live marker
        if (LiveTemp > 0)
        {
            var live = ToScreen(Math.Clamp(LiveTemp, TempMin, TempMax), Math.Clamp(LiveDuty, 0, 100));
            dc.DrawEllipse(LiveBrush, null, live, 4, 4);
            dc.DrawLine(new Pen(LiveBrush, 1) { DashStyle = DashStyles.Dot },
                new Point(live.X, r.Y), new Point(live.X, r.Bottom));
        }
    }

    private void DrawLabel(DrawingContext dc, string text, Point at, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelFace, size, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, at);
    }

    private int HitTestPoint(Point mouse)
    {
        var points = Config.Points;
        for (int i = 0; i < points.Count; i++)
        {
            var p = ToScreen(points[i].TempC, points[i].DutyPct);
            if ((p - mouse).Length <= HitRadius)
            {
                return i;
            }
        }

        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsReadOnly)
        {
            return;
        }

        var mouse = e.GetPosition(this);

        if (e.ClickCount == 2)
        {
            AddPoint(mouse);
            return;
        }

        _dragIndex = HitTestPoint(mouse);
        if (_dragIndex >= 0)
        {
            CaptureMouse();
            InvalidateVisual();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex < 0 || IsReadOnly)
        {
            return;
        }

        var (temp, duty) = ToData(e.GetPosition(this));
        var points = Config.Points.ToList();

        // Keep temperature strictly between neighbors.
        double minTemp = _dragIndex > 0 ? points[_dragIndex - 1].TempC + 1 : TempMin;
        double maxTemp = _dragIndex < points.Count - 1 ? points[_dragIndex + 1].TempC - 1 : TempMax;
        temp = Math.Clamp(temp, minTemp, maxTemp);

        points[_dragIndex] = new FanPoint(Math.Round(temp), Math.Round(duty));
        Config = Config with { Points = MakeMonotonic(points, _dragIndex) };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex >= 0)
        {
            _dragIndex = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            CurveEdited?.Invoke(this, Config);
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (IsReadOnly)
        {
            return;
        }

        int hit = HitTestPoint(e.GetPosition(this));
        if (hit >= 0 && Config.Points.Count > 2)
        {
            var points = Config.Points.ToList();
            points.RemoveAt(hit);
            Config = Config with { Points = points };
            CurveEdited?.Invoke(this, Config);
        }
    }

    private void AddPoint(Point mouse)
    {
        if (Config.Points.Count >= FanCurveConfig.MaxPoints)
        {
            return;
        }

        var (temp, duty) = ToData(mouse);
        var points = Config.Points.ToList();
        int insertAt = points.FindIndex(p => p.TempC > temp);
        if (insertAt < 0)
        {
            insertAt = points.Count;
        }

        points.Insert(insertAt, new FanPoint(Math.Round(temp), Math.Round(duty)));
        Config = Config with { Points = MakeMonotonic(points, insertAt) };
        CurveEdited?.Invoke(this, Config);
    }

    /// <summary>Pushes neighboring duties so the curve never decreases, anchored at the edited index.</summary>
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
