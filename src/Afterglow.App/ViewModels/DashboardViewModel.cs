using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Telemetry;

namespace Afterglow.App.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly uint _deviceIndex;

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

    public ObservableCollectionOfChips ThrottleChips { get; } = new();

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        _deviceIndex = !services.DemoMode && services.Gpus.Count > 0 ? services.Gpus[0].Index : 0;
        services.Telemetry.SnapshotTaken += OnSnapshot;
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
            VramText = $"{used / 1024.0 / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0 / 1024.0:F0} GB";
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
