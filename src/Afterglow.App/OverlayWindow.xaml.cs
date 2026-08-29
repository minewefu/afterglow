using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Settings;
using Afterglow.Core.Telemetry;

namespace Afterglow.App;

/// <summary>
/// Click-through, non-activating, topmost overlay for borderless/windowed games.
/// DWM-composited (no injection): works over windowed, borderless, and
/// fullscreen-optimized games; documented limitation — not over legacy
/// exclusive-fullscreen.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const nint WsExTransparent = 0x20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExToolWindow = 0x80;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private OverlaySettings _settings;
    private uint _deviceIndex;

    public OverlayWindow(AppServices services, OverlaySettings settings)
    {
        InitializeComponent();
        _services = services;
        _settings = settings;
        _deviceIndex = services.SelectedGpu?.Index ?? 0;
        services.SelectedGpuChanged += gpu => _deviceIndex = gpu.Index;

        Opacity = settings.Opacity;
        SourceInitialized += (_, _) => MakeClickThrough();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Loaded += (_, _) => Reposition();
    }

    public void ApplySettings(OverlaySettings settings)
    {
        _settings = settings;
        Opacity = settings.Opacity;
        Reposition();
    }

    private void MakeClickThrough()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        nint style = GetWindowLongPtrW(handle, GwlExstyle);
        _ = SetWindowLongPtrW(handle, GwlExstyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        const int margin = 12;
        switch (_settings.Corner)
        {
            case OverlayCorner.TopRight:
                Left = area.Right - ActualWidth - margin;
                Top = area.Top + margin;
                break;
            case OverlayCorner.BottomLeft:
                Left = area.Left + margin;
                Top = area.Bottom - ActualHeight - margin;
                break;
            case OverlayCorner.BottomRight:
                Left = area.Right - ActualWidth - margin;
                Top = area.Bottom - ActualHeight - margin;
                break;
            default:
                Left = area.Left + margin;
                Top = area.Top + margin;
                break;
        }
    }

    private void Refresh()
    {
        // FPS block
        var stats = _services.FrameMetrics.GetTargetStats();
        bool showFps = _settings.ShowFps && stats is not null;
        FpsRow.Visibility = showFps ? Visibility.Visible : Visibility.Collapsed;
        LowsRow.Visibility = showFps ? Visibility.Visible : Visibility.Collapsed;
        FrametimeGraph.Visibility = showFps && _settings.ShowFrametimeGraph
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (showFps)
        {
            var (app, s) = stats!.Value;
            FpsRow.Text = $"{s.AverageFps:F0} FPS  {app.Application}";
            LowsRow.Text = $"1% {s.Low1Fps:F0}   0.1% {s.Low01Fps:F0}   {s.AverageFrametimeMs:F1} ms";
            if (_settings.ShowFrametimeGraph)
            {
                FrametimeGraph.Values = _services.FrameMetrics.GetTargetFrametimes(240);
            }
        }

        // Sensor block
        var snapshot = _services.Telemetry.HistoryFor(_deviceIndex).Latest;
        var sb = new StringBuilder();
        if (snapshot is not null)
        {
            if (_settings.ShowClock && snapshot.CoreClockMHz is uint clock)
            {
                sb.AppendLine($"GPU  {clock,5} MHz  {snapshot.GpuUtilPct,3}%");
            }

            if (_settings.ShowGpuTemp && snapshot.GpuTempC is uint temp)
            {
                string hot = _settings.ShowHotSpot && snapshot.HotSpotTempC is double hs ? $"  hot {hs:F0}°" : string.Empty;
                string mem = snapshot.MemJunctionTempC is double mj ? $"  mem {mj:F0}°" : string.Empty;
                sb.AppendLine($"TEMP {temp,4}°C{hot}{mem}");
            }

            if (_settings.ShowPower && snapshot.PowerW is double power)
            {
                sb.AppendLine($"PWR  {power,5:F0} W");
            }

            if (_settings.ShowVram && snapshot.VramUsedBytes is ulong vram)
            {
                sb.AppendLine($"VRAM {vram / 1024.0 / 1024 / 1024,5:F1} GB");
            }

            if (_settings.ShowFan && snapshot.MaxFanPercent is uint fan)
            {
                sb.AppendLine($"FAN  {fan,4}%");
            }
        }

        SensorRows.Text = sb.ToString().TrimEnd();
        SensorRows.Visibility = SensorRows.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Throttle line
        if (_settings.ShowThrottle && snapshot?.ThrottleReasons is { } reasons)
        {
            var chips = ThrottleDescriber.Describe(reasons)
                .Where(c => c.Severity != ThrottleDescriber.ThrottleSeverity.Info)
                .Select(c => c.Label)
                .ToList();
            ThrottleRow.Text = chips.Count > 0 ? "⚠ " + string.Join(" · ", chips) : string.Empty;
        }
        else
        {
            ThrottleRow.Text = string.Empty;
        }

        ThrottleRow.Visibility = ThrottleRow.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (IsLoaded)
        {
            Reposition();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
