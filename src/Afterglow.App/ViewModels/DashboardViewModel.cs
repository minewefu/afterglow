using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Telemetry;

namespace Afterglow.App.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private uint _deviceIndex;

    // Hero values
    [ObservableProperty] private string _coreClockText = "—";
    [ObservableProperty] private string _memClockText = "—";
    [ObservableProperty] private string _tempText = "—";
    [ObservableProperty] private string _hotSpotText = "—";
    [ObservableProperty] private string _memJunctionText = "—";
    [ObservableProperty] private string _powerText = "—";
    [ObservableProperty] private string _powerLimitText = string.Empty;
    [ObservableProperty] private string _utilText = "—";
    [ObservableProperty] private string _vramText = "—";
    [ObservableProperty] private double _vramFraction;
    [ObservableProperty] private string _fanText = "—";
    [ObservableProperty] private string _fanRpmText = string.Empty;
    [ObservableProperty] private string _voltageText = "—";
    [ObservableProperty] private string _perfStateText = string.Empty;
    [ObservableProperty] private string _throttleMarginText = string.Empty;
    [ObservableProperty] private string _energyText = string.Empty;

    // FPS strip
    [ObservableProperty] private bool _hasFps;
    [ObservableProperty] private string _fpsAppText = string.Empty;
    [ObservableProperty] private string _fpsText = "—";
    [ObservableProperty] private string _fpsLowsText = string.Empty;
    [ObservableProperty] private IReadOnlyList<double>? _frametimeSeries;

    // Graph series
    [ObservableProperty] private IReadOnlyList<double>? _coreClockSeries;
    [ObservableProperty] private IReadOnlyList<double>? _tempSeries;
    [ObservableProperty] private IReadOnlyList<double>? _hotSpotSeries;
    [ObservableProperty] private IReadOnlyList<double>? _memJunctionSeries;
    [ObservableProperty] private IReadOnlyList<double>? _powerSeries;
    [ObservableProperty] private IReadOnlyList<double>? _utilSeries;
    [ObservableProperty] private IReadOnlyList<double>? _vramSeries;
    [ObservableProperty] private IReadOnlyList<double>? _fanSeries;
    [ObservableProperty] private IReadOnlyList<double>? _voltageSeries;

    /// <summary>Timestamps for the telemetry series above (all share the snapshot ring).</summary>
    [ObservableProperty] private IReadOnlyList<DateTimeOffset>? _seriesTimes;

    // Expanded tile (click a hero card to open its full 10-minute graph)
    [ObservableProperty] private string? _expandedMetric;
    [ObservableProperty] private string _expandedTitle = string.Empty;
    [ObservableProperty] private string _expandedUnit = string.Empty;
    [ObservableProperty] private string _expandedLabel = string.Empty;
    [ObservableProperty] private string _expandedLabel2 = string.Empty;
    [ObservableProperty] private IReadOnlyList<double>? _expandedSeries;
    [ObservableProperty] private IReadOnlyList<double>? _expandedSeries2;
    [ObservableProperty] private IReadOnlyList<DateTimeOffset>? _expandedTimes;
    [ObservableProperty] private System.Windows.Media.Brush? _expandedStroke;
    [ObservableProperty] private System.Windows.Media.Brush? _expandedStroke2;
    [ObservableProperty] private double _expandedFixedMin = double.NaN;
    [ObservableProperty] private double _expandedFixedMax = double.NaN;
    [ObservableProperty] private int _expandedCapacity = 600;

    public bool IsExpanded => ExpandedMetric is not null;

    public bool HasExpandedSecondSeries => ExpandedSeries2 is not null;

    partial void OnExpandedMetricChanged(string? value) => OnPropertyChanged(nameof(IsExpanded));

    partial void OnExpandedSeries2Changed(IReadOnlyList<double>? value) =>
        OnPropertyChanged(nameof(HasExpandedSecondSeries));

    /// <summary>Click on a hero tile: opens its expanded graph, or closes it if already open.</summary>
    public void ToggleExpand(string metricKey)
    {
        ExpandedMetric = ExpandedMetric == metricKey ? null : metricKey;
        RefreshExpanded();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CloseExpanded() => ExpandedMetric = null;

    private static System.Windows.Media.Brush? SeriesBrush(string key) =>
        Application.Current?.TryFindResource(key) as System.Windows.Media.Brush;

    private void RefreshExpanded()
    {
        if (ExpandedMetric is not { } key)
        {
            return;
        }

        ExpandedSeries2 = null;
        ExpandedStroke2 = null;
        ExpandedLabel = string.Empty;
        ExpandedLabel2 = string.Empty;
        ExpandedFixedMin = double.NaN;
        ExpandedFixedMax = double.NaN;
        ExpandedTimes = SeriesTimes;
        ExpandedCapacity = 600;

        switch (key)
        {
            case "clock":
                ExpandedTitle = "CORE CLOCK — 10 MIN";
                ExpandedUnit = "MHz";
                ExpandedSeries = CoreClockSeries;
                ExpandedStroke = SeriesBrush("SeriesClock");
                break;
            case "temp":
                ExpandedTitle = "TEMPERATURES — 10 MIN";
                ExpandedUnit = "°C";
                ExpandedSeries = TempSeries;
                ExpandedSeries2 = MemJunctionSeries;
                ExpandedStroke = SeriesBrush("SeriesTemp");
                ExpandedStroke2 = SeriesBrush("SeriesVram");
                ExpandedLabel = "GPU";
                ExpandedLabel2 = "mem";
                ExpandedFixedMin = 25;
                ExpandedFixedMax = 95;
                break;
            case "power":
                ExpandedTitle = "BOARD POWER — 10 MIN";
                ExpandedUnit = "W";
                ExpandedSeries = PowerSeries;
                ExpandedStroke = SeriesBrush("SeriesPower");
                break;
            case "util":
                ExpandedTitle = "GPU LOAD — 10 MIN";
                ExpandedUnit = "%";
                ExpandedSeries = UtilSeries;
                ExpandedStroke = SeriesBrush("SeriesUtil");
                ExpandedFixedMin = 0;
                ExpandedFixedMax = 100;
                break;
            case "vram":
                ExpandedTitle = "VRAM USED — 10 MIN";
                ExpandedUnit = "GB";
                ExpandedSeries = VramSeries;
                ExpandedStroke = SeriesBrush("SeriesVram");
                ExpandedFixedMin = 0;
                break;
            case "fan":
                ExpandedTitle = "FAN DUTY — 10 MIN";
                ExpandedUnit = "%";
                ExpandedSeries = FanSeries;
                ExpandedStroke = SeriesBrush("SeriesFan");
                ExpandedFixedMin = 0;
                ExpandedFixedMax = 100;
                break;
            case "voltage":
                ExpandedTitle = "CORE VOLTAGE — 10 MIN";
                ExpandedUnit = "mV";
                ExpandedSeries = VoltageSeries;
                ExpandedStroke = SeriesBrush("SeriesVoltage");
                break;
            case "fps":
                ExpandedTitle = "FRAMETIMES — LAST 360 FRAMES";
                ExpandedUnit = "ms";
                ExpandedSeries = FrametimeSeries;
                ExpandedStroke = SeriesBrush("SeriesFps");
                ExpandedTimes = null;      // frame axis, not time — no honest timestamp to show
                ExpandedCapacity = 360;
                break;
            default:
                ExpandedMetric = null;
                break;
        }
    }

    public ObservableCollectionOfChips ThrottleChips { get; } = new();

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        _deviceIndex = !services.DemoMode && services.SelectedGpu is { } gpu ? gpu.Index : 0;
        services.Telemetry.SnapshotTaken += OnSnapshot;
    }

    /// <summary>The UI moved to another GPU: follow it with the next snapshot.</summary>
    public void RebindGpu()
    {
        _deviceIndex = !_services.DemoMode && _services.SelectedGpu is { } gpu ? gpu.Index : 0;
        UpdateSeries();
        RefreshExpanded();
    }

    private void OnSnapshot(GpuSnapshot snapshot)
    {
        if (snapshot.DeviceIndex != _deviceIndex)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() => Update(snapshot));
    }

    private void Update(GpuSnapshot s)
    {
        CoreClockText = s.CoreClockMHz is uint core ? core.ToString("N0") : "—";
        MemClockText = s.MemClockMHz is uint mem ? $"{mem:N0} MHz memory" : string.Empty;
        TempText = s.GpuTempC is uint t ? t.ToString("N0") : "—";
        HotSpotText = s.HotSpotTempC is double hs ? $"{hs:F0}° hot spot" : "hot spot n/a";
        MemJunctionText = s.MemJunctionTempC is double mj ? $"{mj:F0}° memory" : string.Empty;
        PowerText = s.PowerW is double p ? p.ToString("F0") : "—";
        PowerLimitText = (s.PowerAvgW, s.PowerLimitW) switch
        {
            (double avg, double pl) => $"avg {avg:F0} W · limit {pl:F0} W",
            (null, double pl) => $"of {pl:F0} W limit",
            (double avg, null) => $"avg {avg:F0} W",
            _ => string.Empty,
        };
        UtilText = s.GpuUtilPct is uint u ? u.ToString() : "—";
        VoltageText = s.CoreVoltageMv is double v ? $"{v:F0}" : "—";
        PerfStateText = s.PerfState is uint ps ? $"P{ps}" : string.Empty;
        ThrottleMarginText = s.ThrottleMarginC is int m ? $"{m}°C headroom to throttle" : string.Empty;
        EnergyText = s.EnergyWh is double e ? $"{e:F1} Wh session energy" : string.Empty;

        if (s is { VramUsedBytes: ulong used, VramTotalBytes: ulong total } && total > 0)
        {
            // On a shared-memory iGPU the figure is the GPU's allocatable
            // budget carved from system RAM, not dedicated VRAM — say so.
            string shared = s.MemoryIsShared == true ? " shared" : "";
            VramText = $"{used / 1024.0 / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0 / 1024.0:F0} GB{shared}";
            VramFraction = (double)used / total;
        }

        if (s.FanPercents is { Count: > 0 } fans)
        {
            FanText = s.MaxFanPercent?.ToString() ?? "—";
            FanRpmText = s.FanRpms is { Count: > 0 } rpms
                ? string.Join("  ", rpms.Select(r => $"{r}"))+" RPM"
                : string.Join("/", fans.Select(f => $"{f}%"));
        }

        // Throttle chips
        ThrottleChips.Update(s.ThrottleReasons is { } reasons
            ? ThrottleDescriber.Describe(reasons)
            : []);

        UpdateSeries();
        UpdateFps();
        RefreshExpanded();
    }

    private void UpdateSeries()
    {
        var history = _services.Telemetry.HistoryFor(_deviceIndex).GetAll();
        if (history.Length == 0)
        {
            return;
        }

        CoreClockSeries = Extract(history, static s => s.CoreClockMHz);
        TempSeries = Extract(history, static s => s.GpuTempC);
        HotSpotSeries = Extract(history, static s => s.HotSpotTempC);
        MemJunctionSeries = Extract(history, static s => s.MemJunctionTempC);
        PowerSeries = Extract(history, static s => s.PowerW);
        UtilSeries = Extract(history, static s => s.GpuUtilPct);
        VramSeries = Extract(history, static s => s.VramUsedBytes is ulong b ? b / 1024.0 / 1024 / 1024 : null);
        FanSeries = Extract(history, static s => s.MaxFanPercent);
        VoltageSeries = Extract(history, static s => s.CoreVoltageMv);

        var times = new DateTimeOffset[history.Length];
        for (int i = 0; i < history.Length; i++)
        {
            times[i] = history[i].Timestamp;
        }

        SeriesTimes = times;
    }

    private void UpdateFps()
    {
        var stats = _services.FrameMetrics.GetTargetStats();
        if (stats is null)
        {
            HasFps = false;
            return;
        }

        HasFps = true;
        var (app, windowStats) = stats.Value;
        FpsAppText = app.Application;
        FpsText = windowStats.AverageFps.ToString("F0");
        FpsLowsText = $"1% low {windowStats.Low1Fps:F0}   0.1% low {windowStats.Low01Fps:F0}";
        FrametimeSeries = _services.FrameMetrics.GetTargetFrametimes(360);
    }

    private static double[] Extract(GpuSnapshot[] history, Func<GpuSnapshot, double?> selector)
    {
        var result = new double[history.Length];
        for (int i = 0; i < history.Length; i++)
        {
            result[i] = selector(history[i]) ?? double.NaN;
        }

        return result;
    }

    private static double[] Extract(GpuSnapshot[] history, Func<GpuSnapshot, uint?> selector) =>
        Extract(history, s => (double?)selector(s));
}

/// <summary>Bindable, change-minimal collection of throttle chips.</summary>
public sealed class ObservableCollectionOfChips : System.Collections.ObjectModel.ObservableCollection<ThrottleDescriber.ThrottleChip>
{
    private string _lastKey = string.Empty;

    public void Update(IReadOnlyList<ThrottleDescriber.ThrottleChip> chips)
    {
        string key = string.Join('|', chips.Select(c => c.Label));
        if (key == _lastKey)
        {
            return;
        }

        _lastKey = key;
        Clear();
        foreach (var chip in chips)
        {
            Add(chip);
        }
    }
}
