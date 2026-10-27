using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Telemetry;

namespace Afterglow.App.ViewModels;

public partial class DashboardViewModel : ObservableObject, ILivePage
{
    /// <summary>The longest window any dashboard graph draws (the 10-minute graphs at 1 s polling).</summary>
    private const int SeriesCapacity = 600;

    private readonly AppServices _services;
    private uint _deviceIndex;
    private volatile bool _isLive;

    /// <inheritdoc />
    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (_isLive == value)
            {
                return;
            }

            _isLive = value;
            if (value)
            {
                RefreshFromHistory();
            }
        }
    }

    // Hero values
    [ObservableProperty] public partial string CoreClockText { get; set; } = "—";
    [ObservableProperty] public partial string MemClockText { get; set; } = "—";
    [ObservableProperty] public partial string TempText { get; set; } = "—";
    [ObservableProperty] public partial string HotSpotText { get; set; } = "—";
    [ObservableProperty] public partial string MemJunctionText { get; set; } = "—";
    [ObservableProperty] public partial string PowerText { get; set; } = "—";
    [ObservableProperty] public partial string PowerLimitText { get; set; } = string.Empty;

    /// <summary>
    /// "Board power", unless the source says it measured the GPU chip alone
    /// (Intel's chip-energy fallback, R-18): a title must not claim the whole
    /// card for a number that leaves out its VRAM, regulators and fans.
    /// </summary>
    [ObservableProperty] public partial string PowerTitle { get; set; } = "Board power";

    /// <summary>"memory junction", or "VRAM" where the reading is the hottest VRAM sensor (Intel, R-19).</summary>
    [ObservableProperty] public partial string MemSeriesLabel { get; set; } = "memory junction";
    [ObservableProperty] public partial string UtilText { get; set; } = "—";
    [ObservableProperty] public partial string VramText { get; set; } = "—";
    [ObservableProperty] public partial double VramFraction { get; set; }
    [ObservableProperty] public partial string FanText { get; set; } = "—";
    [ObservableProperty] public partial string FanRpmText { get; set; } = string.Empty;
    [ObservableProperty] public partial string VoltageText { get; set; } = "—";
    [ObservableProperty] public partial string PerfStateText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ThrottleMarginText { get; set; } = string.Empty;
    [ObservableProperty] public partial string EnergyText { get; set; } = string.Empty;

    // FPS strip
    [ObservableProperty] public partial bool HasFps { get; set; }
    [ObservableProperty] public partial string FpsAppText { get; set; } = string.Empty;
    [ObservableProperty] public partial string FpsText { get; set; } = "—";
    [ObservableProperty] public partial string FpsLowsText { get; set; } = string.Empty;
    [ObservableProperty] public partial IReadOnlyList<double>? FrametimeSeries { get; set; }

    // Graph series
    [ObservableProperty] public partial IReadOnlyList<double>? CoreClockSeries { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? TempSeries { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? HotSpotSeries { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? MemJunctionSeries { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? PowerSeries { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? UtilSeries { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? VramSeries { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? FanSeries { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? VoltageSeries { get; set; }

    /// <summary>Timestamps for the telemetry series above (all share the snapshot ring).</summary>
    [ObservableProperty] public partial IReadOnlyList<DateTimeOffset>? SeriesTimes { get; set; }

    // Expanded tile (click a hero card to open its full 10-minute graph)
    [ObservableProperty] public partial string? ExpandedMetric { get; set; }
    [ObservableProperty] public partial string ExpandedTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExpandedUnit { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExpandedLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExpandedLabel2 { get; set; } = string.Empty;
    [ObservableProperty] public partial IReadOnlyList<double>? ExpandedSeries { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? ExpandedSeries2 { get; set; }
    [ObservableProperty] public partial IReadOnlyList<DateTimeOffset>? ExpandedTimes { get; set; }
    [ObservableProperty] public partial System.Windows.Media.Brush? ExpandedStroke { get; set; }
    [ObservableProperty] public partial System.Windows.Media.Brush? ExpandedStroke2 { get; set; }
    [ObservableProperty] public partial double ExpandedFixedMin { get; set; } = double.NaN;
    [ObservableProperty] public partial double ExpandedFixedMax { get; set; } = double.NaN;
    [ObservableProperty] public partial int ExpandedCapacity { get; set; } = 600;

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
                ExpandedTitle = "Core clock · last 10 min";
                ExpandedUnit = "MHz";
                ExpandedSeries = CoreClockSeries;
                ExpandedStroke = SeriesBrush("SeriesClock");
                break;
            case "temp":
                ExpandedTitle = "Temperatures · last 10 min";
                ExpandedUnit = "°C";
                ExpandedSeries = TempSeries;
                ExpandedSeries2 = MemJunctionSeries;
                ExpandedStroke = SeriesBrush("SeriesTemp");
                ExpandedStroke2 = SeriesBrush("SeriesVram");
                ExpandedLabel = "GPU";
                ExpandedLabel2 = "mem";
                ExpandedFixedMin = 20;
                ExpandedFixedMax = 100;
                break;
            case "power":
                ExpandedTitle = $"{PowerTitle} · last 10 min";
                ExpandedUnit = "W";
                ExpandedSeries = PowerSeries;
                ExpandedStroke = SeriesBrush("SeriesPower");
                break;
            case "util":
                ExpandedTitle = "GPU load · last 10 min";
                ExpandedUnit = "%";
                ExpandedSeries = UtilSeries;
                ExpandedStroke = SeriesBrush("SeriesUtil");
                ExpandedFixedMin = 0;
                ExpandedFixedMax = 100;
                break;
            case "vram":
                ExpandedTitle = "VRAM used · last 10 min";
                ExpandedUnit = "GB";
                ExpandedSeries = VramSeries;
                ExpandedStroke = SeriesBrush("SeriesVram");
                ExpandedFixedMin = 0;
                break;
            case "fan":
                ExpandedTitle = "Fan duty · last 10 min";
                ExpandedUnit = "%";
                ExpandedSeries = FanSeries;
                ExpandedStroke = SeriesBrush("SeriesFan");
                ExpandedFixedMin = 0;
                ExpandedFixedMax = 100;
                break;
            case "voltage":
                ExpandedTitle = "Core voltage · last 10 min";
                ExpandedUnit = "mV";
                ExpandedSeries = VoltageSeries;
                ExpandedStroke = SeriesBrush("SeriesVoltage");
                break;
            case "fps":
                ExpandedTitle = "Frametimes · last 360 frames";
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
        if (_isLive)
        {
            RefreshFromHistory();
        }
    }

    /// <summary>Catch up at once (page opened, window restored, GPU switched) instead of waiting a tick.</summary>
    private void RefreshFromHistory()
    {
        if (_services.Telemetry.TryHistoryFor(_deviceIndex)?.Latest is { } latest)
        {
            Update(latest);
        }
    }

    private void OnSnapshot(GpuSnapshot snapshot)
    {
        // Hidden in the tray or on another page: nothing to draw, so no
        // dispatcher hop, no string formatting, no series copies.
        if (!_isLive || snapshot.DeviceIndex != _deviceIndex)
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
        PowerTitle = s.PowerIsChipOnly == true ? "GPU chip power" : "Board power";
        MemSeriesLabel = s.MemTempIsVramSensor == true ? "VRAM" : "memory junction";
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
        // The counter is cumulative since the driver loaded (see
        // GpuSnapshot.EnergyWh), not since this Afterglow session started —
        // calling it "session energy" invited the reading that it measured this
        // sitting, when on a machine up for days it is a far larger number.
        EnergyText = s.EnergyWh is double e ? $"{e:F1} Wh since driver load" : string.Empty;

        // These two were the only tiles whose assignment was conditional; their
        // eleven neighbours all fall to "—" when the sensor goes quiet. So one
        // lost tick after a TDR, or switching the selector from a dGPU with fans
        // to an iGPU without, left the PREVIOUS card's VRAM and fan duty on
        // screen next to live telemetry for the new one — a reading from
        // hardware that was not being read.
        if (s is { VramUsedBytes: ulong used, VramTotalBytes: ulong total } && total > 0)
        {
            // On a shared-memory iGPU the figure is the GPU's allocatable
            // budget carved from system RAM, not dedicated VRAM — say so.
            string shared = s.MemoryIsShared == true ? " shared" : "";
            VramText = $"{used / 1024.0 / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0 / 1024.0:F0} GB{shared}";
            VramFraction = (double)used / total;
        }
        else
        {
            VramText = "—";
            VramFraction = 0;
        }

        if (s.FanPercents is { Count: > 0 } fans)
        {
            FanText = s.MaxFanPercent?.ToString() ?? "—";
            FanRpmText = s.FanRpms is { Count: > 0 } rpms
                ? string.Join("  ", rpms.Select(r => $"{r}"))+" RPM"
                : string.Join("/", fans.Select(f => $"{f}%"));
        }
        else
        {
            FanText = "—";
            FanRpmText = string.Empty;
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
        var history = _services.Telemetry.TryHistoryFor(_deviceIndex)?.GetLatest(SeriesCapacity) ?? [];
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
            // Blank the values, not just the visibility flag. A binding or a
            // layout that still shows this row would otherwise render the last
            // app's FPS indefinitely — the same stale reading the tiles above
            // were fixed for.
            HasFps = false;
            FpsAppText = string.Empty;
            FpsText = "—";
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
