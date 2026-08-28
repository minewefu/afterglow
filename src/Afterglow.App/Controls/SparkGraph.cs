using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Afterglow.App.Controls;

/// <summary>
/// Lightweight custom-drawn time-series graph: line + soft area fill + faint
/// gridlines and min/max labels. Push a new array to <see cref="Values"/> each
/// tick; rendering is immediate-mode, no per-point visual tree.
/// Optional second series (<see cref="Values2"/>) shares the scale, and
/// <see cref="HoverEnabled"/> adds a crosshair that reads out the sample under
/// the mouse — with its real age when <see cref="Times"/> carries the sample
/// timestamps (no assumed polling cadence).
/// </summary>
public sealed class SparkGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(SparkGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Values2Property = DependencyProperty.Register(
        nameof(Values2), typeof(IReadOnlyList<double>), typeof(SparkGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimesProperty = DependencyProperty.Register(
        nameof(Times), typeof(IReadOnlyList<DateTimeOffset>), typeof(SparkGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(SparkGraph),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender, OnStrokeChanged));

    public static readonly DependencyProperty Stroke2Property = DependencyProperty.Register(
        nameof(Stroke2), typeof(Brush), typeof(SparkGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnStrokeChanged));

    public static readonly DependencyProperty FixedMinProperty = DependencyProperty.Register(
        nameof(FixedMin), typeof(double), typeof(SparkGraph),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FixedMaxProperty = DependencyProperty.Register(
        nameof(FixedMax), typeof(double), typeof(SparkGraph),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowScaleProperty = DependencyProperty.Register(
        nameof(ShowScale), typeof(bool), typeof(SparkGraph),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(SparkGraph),
        new FrameworkPropertyMetadata(120, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverEnabledProperty = DependencyProperty.Register(
        nameof(HoverEnabled), typeof(bool), typeof(SparkGraph),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(SparkGraph),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SparkGraph),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty Label2Property = DependencyProperty.Register(
        nameof(Label2), typeof(string), typeof(SparkGraph),
        new FrameworkPropertyMetadata(string.Empty));

    private static readonly Brush GridBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)));
    private static readonly Brush LabelBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)));
    private static readonly Brush CrosshairBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)));
    private static readonly Brush PillBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(216, 12, 15, 20)));
    private static readonly Brush PillTextBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)));
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private Pen? _linePen;
    private Pen? _linePen2;
    private Brush? _fillBrush;
    private Brush? _fillBrush2;
    private double? _hoverX;

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Optional second series drawn on the same scale (e.g. memory junction over GPU temp).</summary>
    public IReadOnlyList<double>? Values2
    {
        get => (IReadOnlyList<double>?)GetValue(Values2Property);
        set => SetValue(Values2Property, value);
    }

    /// <summary>
    /// Sample timestamps matching <see cref="Values"/> (tail-aligned when the
    /// counts briefly differ mid-update). When present, the hover readout shows
    /// the sample's real age instead of assuming a polling cadence.
    /// </summary>
    public IReadOnlyList<DateTimeOffset>? Times
    {
        get => (IReadOnlyList<DateTimeOffset>?)GetValue(TimesProperty);
        set => SetValue(TimesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Stroke2
    {
        get => (Brush?)GetValue(Stroke2Property);
        set => SetValue(Stroke2Property, value);
    }

    /// <summary>Fixed lower bound; NaN = automatic.</summary>
    public double FixedMin
    {
        get => (double)GetValue(FixedMinProperty);
        set => SetValue(FixedMinProperty, value);
    }

    /// <summary>Fixed upper bound; NaN = automatic.</summary>
    public double FixedMax
    {
        get => (double)GetValue(FixedMaxProperty);
        set => SetValue(FixedMaxProperty, value);
    }

    public bool ShowScale
    {
        get => (bool)GetValue(ShowScaleProperty);
        set => SetValue(ShowScaleProperty, value);
    }

    /// <summary>How many trailing points to draw (the graph's time span).</summary>
    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    /// <summary>Crosshair + value readout under the mouse.</summary>
    public bool HoverEnabled
    {
        get => (bool)GetValue(HoverEnabledProperty);
        set => SetValue(HoverEnabledProperty, value);
    }

    /// <summary>Unit suffix for the hover readout ("MHz", "°C", "W"…).</summary>
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    /// <summary>Series name shown in the hover readout when two series are present.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Label2
    {
        get => (string)GetValue(Label2Property);
        set => SetValue(Label2Property, value);
    }

    private static void OnStrokeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (SparkGraph)d;
        graph._linePen = null;
        graph._linePen2 = null;
        graph._fillBrush = null;
        graph._fillBrush2 = null;
    }

    private static Brush MakeFrozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (HoverEnabled)
        {
            _hoverX = e.GetPosition(this).X;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverX is not null)
        {
            _hoverX = null;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawingContext dc = drawingContext;
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 4 || height < 4)
        {
            return;
        }

        // Hit-test/clip area (transparent background so mouse events land).
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var all = Values;
        if (all is not { Count: > 1 })
        {
            return;
        }

        var second = Values2;
        int capacity = Math.Max(2, Capacity);
        int count = Math.Min(all.Count, capacity);
        int offset = all.Count - count;

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        ScanRange(all, all.Count - count, ref min, ref max);
        if (second is { Count: > 1 })
        {
            ScanRange(second, Math.Max(0, second.Count - count), ref min, ref max);
        }

        if (double.IsInfinity(min) || double.IsInfinity(max))
        {
            return;
        }

        if (!double.IsNaN(FixedMin))
        {
            min = FixedMin;
        }

        if (!double.IsNaN(FixedMax))
        {
            max = FixedMax;
        }

        if (max - min < 1e-9)
        {
            max = min + 1;
        }
        else if (double.IsNaN(FixedMin) && double.IsNaN(FixedMax))
        {
            double pad = (max - min) * 0.08;
            min -= pad;
            max += pad;
        }

        // Gridlines.
        for (int g = 1; g <= 3; g++)
        {
            double y = height * g / 4.0;
            dc.DrawLine(new Pen(GridBrush, 1), new Point(0, y), new Point(width, y));
        }

        EnsureBrushes();

        // Points are laid out so the newest sample is at the right edge and a full
        // buffer spans the whole width.
        double stepX = width / (capacity - 1);

        double MapY(double v) =>
            height - ((Math.Clamp(double.IsNaN(v) ? min : v, min, max) - min) / (max - min) * (height - 2)) - 1;

        void DrawSeries(IReadOnlyList<double> series, Pen pen, Brush fill)
        {
            int n = Math.Min(series.Count, capacity);
            int off = series.Count - n;
            if (n < 2)
            {
                return;
            }

            double sx = width - ((n - 1) * stepX);
            var lineGeometry = new StreamGeometry();
            var fillGeometry = new StreamGeometry();
            using (var line = lineGeometry.Open())
            using (var fill2 = fillGeometry.Open())
            {
                var p0 = new Point(sx, MapY(series[off]));
                line.BeginFigure(p0, false, false);
                fill2.BeginFigure(new Point(p0.X, height), true, true);
                fill2.LineTo(p0, false, false);
                for (int i = 1; i < n; i++)
                {
                    var p = new Point(sx + (i * stepX), MapY(series[off + i]));
                    line.LineTo(p, true, false);
                    fill2.LineTo(p, false, false);
                }

                fill2.LineTo(new Point(width, height), false, false);
            }

            lineGeometry.Freeze();
            fillGeometry.Freeze();
            dc.DrawGeometry(fill, null, fillGeometry);
            dc.DrawGeometry(null, pen, lineGeometry);
        }

        DrawSeries(all, _linePen!, _fillBrush!);
        if (second is { Count: > 1 } && _linePen2 is not null)
        {
            DrawSeries(second, _linePen2, _fillBrush2!);
        }

        if (ShowScale)
        {
            DrawLabel(dc, Format(max), new Point(4, 2));
            DrawLabel(dc, Format(min), new Point(4, height - 15));
        }

        if (HoverEnabled && _hoverX is double hx)
        {
            DrawHover(dc, all, second, hx, width, height, stepX, count, offset, MapY);
        }
    }

    private void DrawHover(
        DrawingContext dc, IReadOnlyList<double> all, IReadOnlyList<double>? second,
        double hx, double width, double height, double stepX, int count, int offset,
        Func<double, double> mapY)
    {
        double startX = width - ((count - 1) * stepX);
        int i = (int)Math.Round((hx - startX) / stepX);
        i = Math.Clamp(i, 0, count - 1);
        int absIdx = offset + i;
        double snappedX = startX + (i * stepX);

        dc.DrawLine(new Pen(CrosshairBrush, 1), new Point(snappedX, 0), new Point(snappedX, height));

        double v1 = all[absIdx];
        if (!double.IsNaN(v1))
        {
            dc.DrawEllipse(Stroke, null, new Point(snappedX, mapY(v1)), 3, 3);
        }

        // Second series, tail-aligned (both series end at "now").
        double? v2 = null;
        if (second is { Count: > 1 })
        {
            int idx2 = second.Count - (all.Count - absIdx);
            if (idx2 >= 0 && idx2 < second.Count)
            {
                v2 = second[idx2];
                if (!double.IsNaN(v2.Value) && Stroke2 is { } s2)
                {
                    dc.DrawEllipse(s2, null, new Point(snappedX, mapY(v2.Value)), 3, 3);
                }
            }
        }

        string unit = Unit.Length > 0 ? $" {Unit}" : string.Empty;
        string valueLine = v2 is null
            ? $"{Format(v1)}{unit}"
            : $"{Prefix(Label)}{Format(v1)} · {Prefix(Label2)}{Format(v2.Value)}{unit}";

        string text = valueLine;
        var times = Times;
        if (times is { Count: > 1 })
        {
            int timeIdx = times.Count - (all.Count - absIdx);
            if (timeIdx >= 0 && timeIdx < times.Count)
            {
                var age = DateTimeOffset.Now - times[timeIdx];
                string ago = age.TotalSeconds < 2
                    ? "now"
                    : $"−{(int)age.TotalMinutes}:{Math.Max(0, age.Seconds):00}";
                text = valueLine + "\n" + ago;
            }
        }

        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 11.5, PillTextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { TextAlignment = TextAlignment.Left };

        double px = snappedX + 10;
        if (px + formatted.Width + 12 > width)
        {
            px = snappedX - formatted.Width - 16;
        }

        px = Math.Max(2, px);
        var pill = new Rect(px - 3, 4, formatted.Width + 12, formatted.Height + 6);
        dc.DrawRoundedRectangle(PillBrush, null, pill, 5, 5);
        dc.DrawText(formatted, new Point(px + 3, 7));
    }

    private static string Prefix(string label) => label.Length > 0 ? label + " " : string.Empty;

    private static void ScanRange(IReadOnlyList<double> series, int from, ref double min, ref double max)
    {
        for (int i = from; i < series.Count; i++)
        {
            double v = series[i];
            if (double.IsNaN(v))
            {
                continue;
            }

            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
    }

    private void EnsureBrushes()
    {
        if (_linePen is null)
        {
            _linePen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
            _linePen.Freeze();
        }

        if (_fillBrush is null)
        {
            _fillBrush = MakeFill(Stroke);
        }

        if (Stroke2 is { } stroke2)
        {
            if (_linePen2 is null)
            {
                _linePen2 = new Pen(stroke2, 1.6) { LineJoin = PenLineJoin.Round };
                _linePen2.Freeze();
            }

            _fillBrush2 ??= MakeFill(stroke2);
        }
    }

    private static LinearGradientBrush MakeFill(Brush stroke)
    {
        Color color = stroke is SolidColorBrush solid ? solid.Color : Colors.Orange;
        var gradient = new LinearGradientBrush(
            Color.FromArgb(70, color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B),
            90.0);
        gradient.Freeze();
        return gradient;
    }

    private void DrawLabel(DrawingContext dc, string text, Point at)
    {
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 10, LabelBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, at);
    }

    private static string Format(double value) =>
        double.IsNaN(value)
            ? "—"
            : Math.Abs(value) >= 1000
                ? value.ToString("#,##0", CultureInfo.InvariantCulture)
                : value.ToString("0.#", CultureInfo.InvariantCulture);
}
