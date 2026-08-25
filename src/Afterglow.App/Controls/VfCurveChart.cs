using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Afterglow.Core.Tuning;

namespace Afterglow.App.Controls;

/// <summary>
/// Voltage/frequency curve chart: the measured curve with hit-density shading
/// (how often the GPU actually sat at each point), the live operating point, and
/// a click-to-pick target used to compute an undervolt.
/// X axis = core voltage (mV), Y axis = core clock (MHz).
/// </summary>
public sealed class VfCurveChart : FrameworkElement
{
    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve), typeof(IReadOnlyList<VfBin>), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakSamplesProperty = DependencyProperty.Register(
        nameof(PeakSamples), typeof(long), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LiveVoltageProperty = DependencyProperty.Register(
        nameof(LiveVoltage), typeof(double), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LiveClockProperty = DependencyProperty.Register(
        nameof(LiveClock), typeof(double), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetVoltageProperty = DependencyProperty.Register(
        nameof(TargetVoltage), typeof(double), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetClockProperty = DependencyProperty.Register(
        nameof(TargetClock), typeof(double), typeof(VfCurveChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)));
    private static readonly Brush CurveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF)));
    private static readonly Brush LiveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3C)));
    private static readonly Brush TargetBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x46, 0xC3, 0x6B)));
    private static readonly Brush EmptyBrush = Freeze(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)));
    private static readonly Typeface LabelFace = new("Segoe UI");

    private double _minV = 700, _maxV = 1200, _minF = 1000, _maxF = 3200;

    public IReadOnlyList<VfBin>? Curve
    {
        get => (IReadOnlyList<VfBin>?)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public long PeakSamples
    {
        get => (long)GetValue(PeakSamplesProperty);
        set => SetValue(PeakSamplesProperty, value);
    }

    public double LiveVoltage
    {
        get => (double)GetValue(LiveVoltageProperty);
        set => SetValue(LiveVoltageProperty, value);
    }

    public double LiveClock
    {
        get => (double)GetValue(LiveClockProperty);
        set => SetValue(LiveClockProperty, value);
    }

    public double TargetVoltage
    {
        get => (double)GetValue(TargetVoltageProperty);
        set => SetValue(TargetVoltageProperty, value);
    }

    public double TargetClock
    {
        get => (double)GetValue(TargetClockProperty);
        set => SetValue(TargetClockProperty, value);
    }

    /// <summary>Raised when the user picks a target point (voltage mV, clock MHz).</summary>
    public event EventHandler<(double VoltageMv, double ClockMHz)>? TargetPicked;

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private Rect PlotRect => new(46, 10, Math.Max(10, ActualWidth - 58), Math.Max(10, ActualHeight - 36));

    private Point ToScreen(double voltageMv, double clockMHz)
    {
        var r = PlotRect;
        double x = r.X + ((voltageMv - _minV) / (_maxV - _minV) * r.Width);
        double y = r.Bottom - ((clockMHz - _minF) / (_maxF - _minF) * r.Height);
        return new Point(x, y);
    }

    private (double VoltageMv, double ClockMHz) ToData(Point p)
    {
        var r = PlotRect;
        double v = _minV + ((p.X - r.X) / r.Width * (_maxV - _minV));
        double f = _minF + ((r.Bottom - p.Y) / r.Height * (_maxF - _minF));
        return (Math.Clamp(v, _minV, _maxV), Math.Clamp(f, _minF, _maxF));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawingContext dc = drawingContext;
        var r = PlotRect;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (r.Width < 30 || r.Height < 30)
        {
            return;
        }

        var curve = Curve;
        if (curve is { Count: > 1 })
        {
            // Auto-range with padding, snapped to readable steps.
            double vMin = curve.Min(p => p.VoltageMv);
            double vMax = curve.Max(p => p.VoltageMv);
            double fMin = curve.Min(p => p.MaxClockMHz);
            double fMax = curve.Max(p => p.MaxClockMHz);
            _minV = Math.Floor((vMin - 25) / 25) * 25;
            _maxV = Math.Ceiling((vMax + 25) / 25) * 25;
            _minF = Math.Floor((fMin - 100) / 100) * 100;
            _maxF = Math.Ceiling((fMax + 100) / 100) * 100;
            if (_maxV - _minV < 50)
            {
                _maxV = _minV + 50;
            }

            if (_maxF - _minF < 200)
            {
                _maxF = _minF + 200;
            }
        }

        var gridPen = new Pen(GridBrush, 1);

        // Axes
        for (double v = _minV; v <= _maxV; v += Math.Max(25, Math.Round((_maxV - _minV) / 8 / 25) * 25))
        {
            var p = ToScreen(v, _minF);
            dc.DrawLine(gridPen, new Point(p.X, r.Y), new Point(p.X, r.Bottom));
            DrawLabel(dc, $"{v:F0}", new Point(p.X - 12, r.Bottom + 5), 10);
        }

        for (int i = 0; i <= 4; i++)
        {
            double f = _minF + ((_maxF - _minF) * i / 4);
            var p = ToScreen(_minV, f);
            dc.DrawLine(gridPen, new Point(r.X, p.Y), new Point(r.Right, p.Y));
            DrawLabel(dc, $"{f:F0}", new Point(4, p.Y - 7), 10);
        }

        DrawLabel(dc, "mV", new Point(r.Right - 16, r.Bottom + 5), 10);

        if (curve is not { Count: > 1 })
        {
            DrawLabel(dc,
                "No curve data yet — run the GPU under load (a game, or the burn test) and the curve draws itself.",
                new Point(r.X + 12, r.Y + (r.Height / 2)), 12, EmptyBrush);
            return;
        }

        // Hit-density bars: how much time the GPU spent at each voltage.
        long peak = Math.Max(1, PeakSamples);
        foreach (var bin in curve)
        {
            double intensity = Math.Min(1.0, bin.Samples / (double)peak);
            if (intensity <= 0.01)
            {
                continue;
            }

            var top = ToScreen(bin.VoltageMv, bin.MaxClockMHz);
            double barWidth = Math.Max(2, r.Width / curve.Count * 0.8);
            var brush = new SolidColorBrush(Color.FromArgb((byte)(20 + (intensity * 90)), 0x58, 0xA6, 0xFF));
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(top.X - (barWidth / 2), top.Y, barWidth, r.Bottom - top.Y));
        }

        // Measured curve line.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(ToScreen(curve[0].VoltageMv, curve[0].MaxClockMHz), false, false);
            for (int i = 1; i < curve.Count; i++)
            {
                ctx.LineTo(ToScreen(curve[i].VoltageMv, curve[i].MaxClockMHz), true, false);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(CurveBrush, 2) { LineJoin = PenLineJoin.Round }, geometry);

        foreach (var bin in curve)
        {
            dc.DrawEllipse(CurveBrush, null, ToScreen(bin.VoltageMv, bin.MaxClockMHz), 2.5, 2.5);
        }

        // Target marker + crosshair.
        if (TargetVoltage > 0 && TargetClock > 0)
        {
            var t = ToScreen(TargetVoltage, TargetClock);
            var dashed = new Pen(TargetBrush, 1) { DashStyle = DashStyles.Dash };
            dc.DrawLine(dashed, new Point(r.X, t.Y), new Point(r.Right, t.Y));
            dc.DrawLine(dashed, new Point(t.X, r.Y), new Point(t.X, r.Bottom));
            dc.DrawEllipse(null, new Pen(TargetBrush, 2.5), t, 6, 6);
            DrawLabel(dc, $"target {TargetClock:F0} MHz @ {TargetVoltage:F0} mV",
                new Point(Math.Min(t.X + 10, r.Right - 165), t.Y - 18), 11, TargetBrush);
        }

        // Live operating point.
        if (LiveVoltage > 0 && LiveClock > 0)
        {
            var live = ToScreen(
                Math.Clamp(LiveVoltage, _minV, _maxV),
                Math.Clamp(LiveClock, _minF, _maxF));
            dc.DrawEllipse(LiveBrush, null, live, 4.5, 4.5);
            DrawLabel(dc, "now", new Point(live.X + 8, live.Y - 16), 10, LiveBrush);
        }
    }

    private void DrawLabel(DrawingContext dc, string text, Point at, double size, Brush? brush = null)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelFace, size, brush ?? LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, at);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Pick(e.GetPosition(this));
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured)
        {
            Pick(e.GetPosition(this));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    private void Pick(Point position)
    {
        if (Curve is not { Count: > 1 })
        {
            return;
        }

        var (voltage, clock) = ToData(position);
        TargetPicked?.Invoke(this, (voltage, clock));
    }
}
