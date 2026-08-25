using System.Windows;
using System.Windows.Media;

namespace Afterglow.App;

/// <summary>
/// Diagnostic window (`--present-storm`) that invalidates every composition
/// frame, so the app presents at the monitor refresh rate. Used to validate the
/// PresentMon capture pipeline without needing a game.
/// </summary>
public sealed class PresentStormWindow : Window
{
    private readonly RotateTransform _rotation = new();

    public PresentStormWindow()
    {
        Title = "Afterglow present storm (capture test)";
        Width = 420;
        Height = 320;
        Background = Brushes.Black;

        var gradient = new LinearGradientBrush(
            Color.FromRgb(0xFF, 0x8A, 0x3C),
            Color.FromRgb(0x12, 0x16, 0x1D),
            0);
        var square = new System.Windows.Shapes.Rectangle
        {
            Width = 200,
            Height = 200,
            Fill = gradient,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _rotation,
        };
        Content = square;

        CompositionTarget.Rendering += (_, _) => _rotation.Angle = (_rotation.Angle + 3) % 360;
    }
}
