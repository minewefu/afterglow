using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Afterglow.App.Controls;

/// <summary>
/// Lightweight custom-drawn time-series graph: line + soft area fill + faint
/// gridlines and min/max labels. Push a new array to <see cref="Values"/> each
/// tick; rendering is immediate-mode, no per-point visual tree.
/// </summary>
public sealed class SparkGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(SparkGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(SparkGraph),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender, OnStrokeChanged));

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

    private static readonly Brush GridBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)));
    private static readonly Brush LabelBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)));
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private Pen? _linePen;
    private Brush? _fillBrush;

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
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

    private static void OnStrokeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (SparkGraph)d;
        graph._linePen = null;
        graph._fillBrush = null;
    }

    private static Brush MakeFrozen(Brush brush)
    {
        brush.Freeze();
        return brush;
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

        // Hit-test/clip area (transparent background so tooltips work).
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var all = Values;
        if (all is not { Count: > 1 })
        {
            return;
        }

        int capacity = Math.Max(2, Capacity);
        int count = Math.Min(all.Count, capacity);
        int offset = all.Count - count;

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            double v = all[offset + i];
            if (double.IsNaN(v))
            {
                continue;
            }

            min = Math.Min(min, v);
            max = Math.Max(max, v);
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
        double startX = width - ((count - 1) * stepX);

        Point Map(int i)
        {
            double v = all[offset + i];
            if (double.IsNaN(v))
            {
                v = min;
            }

            double x = startX + (i * stepX);
            double y = height - ((Math.Clamp(v, min, max) - min) / (max - min) * (height - 2)) - 1;
            return new Point(x, y);
        }

        var lineGeometry = new StreamGeometry();
        var fillGeometry = new StreamGeometry();
        using (var line = lineGeometry.Open())
        using (var fill = fillGeometry.Open())
        {
            var p0 = Map(0);
            line.BeginFigure(p0, false, false);
            fill.BeginFigure(new Point(p0.X, height), true, true);
            fill.LineTo(p0, false, false);
            for (int i = 1; i < count; i++)
            {
                var p = Map(i);
                line.LineTo(p, true, false);
                fill.LineTo(p, false, false);
            }

            fill.LineTo(new Point(width, height), false, false);
        }

        lineGeometry.Freeze();
        fillGeometry.Freeze();
        dc.DrawGeometry(_fillBrush, null, fillGeometry);
        dc.DrawGeometry(null, _linePen, lineGeometry);

        if (ShowScale)
        {
            DrawLabel(dc, Format(max), new Point(4, 2));
            DrawLabel(dc, Format(min), new Point(4, height - 15));
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
            Color color = Stroke is SolidColorBrush solid ? solid.Color : Colors.Orange;
            var gradient = new LinearGradientBrush(
                Color.FromArgb(70, color.R, color.G, color.B),
                Color.FromArgb(0, color.R, color.G, color.B),
                90.0);
            gradient.Freeze();
            _fillBrush = gradient;
        }
    }

    private void DrawLabel(DrawingContext dc, string text, Point at)
    {
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 10, LabelBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, at);
    }

    private static string Format(double value) =>
        Math.Abs(value) >= 1000
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
}
