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

    // Shared, frozen drawing resources: the editor redraws every telemetry tick
    // while the Fans page is open (the live marker moves).
    private static readonly Pen GridPen = Freeze(new Pen(GridBrush, 1));
    private static readonly Pen CurvePen = Freeze(new Pen(CurveBrush, 2) { LineJoin = PenLineJoin.Round });
    private static readonly Pen LivePen = Freeze(new Pen(LiveBrush, 1) { DashStyle = DashStyles.Dot });

    // Axis labels are the same few dozen strings every redraw.
    private readonly Dictionary<(string Text, double Size), FormattedText> _labelCache = [];
    private double _labelCacheDpi;

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

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
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
            dc.DrawLine(GridPen, new Point(p.X, r.Y), new Point(p.X, r.Bottom));
            DrawLabel(dc, $"{temp}°", new Point(p.X - 8, r.Bottom + 4), 10);
        }

        for (int duty = 0; duty <= 100; duty += 25)
        {
            var p = ToScreen(TempMin, duty);
            dc.DrawLine(GridPen, new Point(r.X, p.Y), new Point(r.Right, p.Y));
            DrawLabel(dc, $"{duty}%", new Point(4, p.Y - 7), 10);
        }

        // Curve
        var points = config.Points;
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
        dc.DrawGeometry(null, CurvePen, geometry);

        // Points
        for (int i = 0; i < points.Count; i++)
        {
            var p = ToScreen(points[i].TempC, points[i].DutyPct);
            dc.DrawEllipse(PointFill, CurvePen, p, i == _dragIndex ? 7 : 5, i == _dragIndex ? 7 : 5);
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
            dc.DrawLine(LivePen, new Point(live.X, r.Y), new Point(live.X, r.Bottom));
        }
    }

    private void DrawLabel(DrawingContext dc, string text, Point at, double size)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi != _labelCacheDpi || _labelCache.Count > 128)
        {
            _labelCache.Clear();
            _labelCacheDpi = dpi;
        }

        if (!_labelCache.TryGetValue((text, size), out var formatted))
        {
            formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelFace, size, LabelBrush, dpi);
            _labelCache[(text, size)] = formatted;
        }

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
            // Moves only count while captured (see OnMouseMove), and losing the
            // capture is what ends the drag; no capture, no drag.
            if (!CaptureMouse())
            {
                _dragIndex = -1;
            }

            InvalidateVisual();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // Only while the press that started the drag holds the capture: a drag
        // whose button-up went elsewhere must not turn hovering into edits.
        if (_dragIndex < 0 || IsReadOnly || !IsMouseCaptured)
        {
            return;
        }

        // The edit rules (whole degrees, a degree between neighbours, never
        // impossible clamp bounds) live in Core, where they are tested (R-40).
        var (temp, duty) = ToData(e.GetPosition(this));
        if (FanCurveEditing.MovePoint(Config.Points, _dragIndex, temp, duty, TempMin, TempMax) is { } moved)
        {
            Config = Config with { Points = moved };
        }
        else
        {
            // The curve was replaced under the drag (a profile applied by a
            // shortcut, say) and this point is gone: drop the drag, which has no
            // edit left to report.
            _dragIndex = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    /// <summary>
    /// Ends a drag however the capture went: the button released here, or taken
    /// away (Alt+Tab, a dialog). The curve already shows the dragged point, so
    /// the edit is reported either way.
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragIndex >= 0)
        {
            _dragIndex = -1;
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
        if (hit >= 0 && FanCurveEditing.RemovePoint(Config.Points, hit) is { } remaining)
        {
            Config = Config with { Points = remaining };
            CurveEdited?.Invoke(this, Config);
        }
    }

    private void AddPoint(Point mouse)
    {
        // A double-click on an existing point (or between two a degree apart)
        // adds nothing rather than a duplicate temperature (R-40).
        var (temp, duty) = ToData(mouse);
        if (FanCurveEditing.AddPoint(Config.Points, temp, duty) is { } added)
        {
            Config = Config with { Points = added };
            CurveEdited?.Invoke(this, Config);
        }
    }
}
