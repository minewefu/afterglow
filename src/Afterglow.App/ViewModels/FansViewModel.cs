using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core.Fans;
using Afterglow.Core.Hardware;
using Afterglow.Core.Telemetry;

namespace Afterglow.App.ViewModels;

public partial class FansViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly GpuContext? _gpu;
    private readonly FanControlService? _fanControl;

    [ObservableProperty]
    private int _modeIndex; // 0 auto, 1 fixed, 2 curve

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FixedDutyLabel))]
    private double _fixedDuty = 50;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _liveText = "—";

    [ObservableProperty]
    private double _liveTemp;

    [ObservableProperty]
    private double _liveDuty;

    // Curve settings (bound to the editor + parameter controls)
    [ObservableProperty]
    private FanCurveConfig _curve = new();

    [ObservableProperty]
    private int _tempSourceIndex;

    [ObservableProperty]
    private double _hysteresis = 3;

    [ObservableProperty]
    private bool _zeroRpmEnabled = true;

    [ObservableProperty]
    private double _zeroRpmBelow = 45;

    public string FixedDutyLabel => $"{FixedDuty:F0}%";

    /// <summary>NVAPI cooler ids for the per-fan buttons (typically 1..3).</summary>
    public IReadOnlyList<string> FanIds { get; private set; } = [];

    partial void OnTempSourceIndexChanged(int value) => PersistFanSettings();

    partial void OnHysteresisChanged(double value) => PersistFanSettings();

    partial void OnZeroRpmEnabledChanged(bool value) => PersistFanSettings();

    partial void OnZeroRpmBelowChanged(double value) => PersistFanSettings();

    public bool CanControl => _services.DemoMode || (_services.IsElevated && _gpu is not null);

    public string GateText => CanControl
        ? string.Empty
        : "Fan control needs administrator rights.";

    public bool HasMemJunction { get; }

    public FansViewModel(AppServices services)
    {
        _services = services;
        _gpu = services.Gpus.Count > 0 ? services.Gpus[0] : null;
        if (_gpu is not null)
        {
            services.FanControl.TryGetValue(_gpu.Index, out _fanControl);
        }

        HasMemJunction = services.DemoMode ||
            (_gpu?.Nvapi?.GetPrivateThermals().MemJunctionC is not null);

        if (_gpu?.Nvapi is not null &&
            _gpu.Nvapi.TryGetFanStatus(out var fanInfos) == Core.Interop.Nvapi.NvapiStatus.Ok)
        {
            FanIds = fanInfos.Select(f => f.CoolerId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        }
        else if (services.DemoMode)
        {
            FanIds = ["1", "2", "3"];
        }

        // Restore the persisted configuration into the editor.
        var saved = services.Settings.Fans;
        _loadingSettings = true;
        ModeIndex = saved.Mode switch
        {
            "fixed" => 1,
            "curve" => 2,
            _ => 0,
        };
        FixedDuty = saved.FixedDutyPct;
        Curve = saved.Curve;
        TempSourceIndex = (int)saved.Curve.TempSource;
        Hysteresis = saved.Curve.HysteresisC;
        ZeroRpmEnabled = saved.Curve.ZeroRpmBelowC > 0;
        ZeroRpmBelow = saved.Curve.ZeroRpmBelowC > 0 ? saved.Curve.ZeroRpmBelowC : 45;
        _loadingSettings = false;

        services.Telemetry.SnapshotTaken += OnSnapshot;
    }

    private bool _loadingSettings;

    /// <summary>Current configuration for persistence and for "save into profile".</summary>
    public (Core.Profiles.FanMode Mode, uint FixedPct, FanCurveConfig Curve) CurrentConfig => (
        ModeIndex switch
        {
            1 => Core.Profiles.FanMode.Fixed,
            2 => Core.Profiles.FanMode.Curve,
            _ => Core.Profiles.FanMode.Auto,
        },
        (uint)FixedDuty,
        BuildConfig());

    private void PersistFanSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        var (mode, fixedPct, curve) = CurrentConfig;
        _services.UpdateSettings(s => s with
        {
            Fans = new Core.Settings.FanSettings
            {
                Mode = mode switch
                {
                    Core.Profiles.FanMode.Fixed => "fixed",
                    Core.Profiles.FanMode.Curve => "curve",
                    _ => "auto",
                },
                FixedDutyPct = fixedPct,
                Curve = curve,
            },
        });
    }

    private void OnSnapshot(GpuSnapshot snapshot)
    {
        if (_gpu is not null && snapshot.DeviceIndex != _gpu.Index)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var fans = snapshot.FanPercents;
            var rpms = snapshot.FanRpms;
            LiveText = fans is { Count: > 0 }
                ? string.Join("   ", fans.Select((f, i) =>
                    $"Fan {i + 1}: {f}%{(rpms is not null && i < rpms.Count ? $" ({rpms[i]} RPM)" : string.Empty)}"))
                : "—";

            LiveTemp = SelectTemp(snapshot) ?? 0;
            LiveDuty = snapshot.MaxFanPercent ?? 0;
        });
    }

    private double? SelectTemp(GpuSnapshot s) => (FanTempSource)TempSourceIndex switch
    {
        FanTempSource.HotSpot => s.HotSpotTempC ?? s.GpuTempC,
        FanTempSource.MemJunction => s.MemJunctionTempC ?? s.GpuTempC,
        _ => s.GpuTempC,
    };

    /// <summary>Current curve config assembled from editor points + parameter controls.</summary>
    public FanCurveConfig BuildConfig() => Curve with
    {
        TempSource = (FanTempSource)TempSourceIndex,
        HysteresisC = Hysteresis,
        ZeroRpmBelowC = ZeroRpmEnabled ? ZeroRpmBelow : 0,
    };

    [RelayCommand]
    private void ApplyMode()
    {
        if (_fanControl is null)
        {
            StatusText = _services.DemoMode
                ? "Applied (demo mode)."
                : "No controllable GPU.";
            return;
        }

        try
        {
            switch (ModeIndex)
            {
                case 1:
                    uint duty = Core.Tuning.TuningMath.NormalizeFixedFanDuty(
                        (uint)FixedDuty, _gpu?.Tuner.Capabilities.FanMinDutyPct ?? 30);
                    _fanControl.SetFixed((uint)FixedDuty);
                    StatusText = duty == (uint)FixedDuty
                        ? $"Fixed {duty}% applied to all fans."
                        : $"Fixed {duty}% applied (requested {FixedDuty:F0}%, raised to the hardware minimum spin duty).";
                    break;
                case 2:
                    _fanControl.SetCurve(BuildConfig());
                    StatusText = "Curve active — Afterglow is driving the fans.";
                    break;
                default:
                    _fanControl.SetAuto();
                    StatusText = "Firmware (auto) fan control restored.";
                    break;
            }

            PersistFanSettings();
        }
        catch (ArgumentException ex)
        {
            StatusText = $"Invalid curve: {ex.Message}";
        }
    }

    /// <summary>Called by the curve editor when points change.</summary>
    public void OnCurveEdited(FanCurveConfig updated)
    {
        Curve = updated;
        if (ModeIndex == 2 && _fanControl is not null && _fanControl.Mode == FanControlMode.Curve)
        {
            try
            {
                _fanControl.SetCurve(BuildConfig());
                StatusText = "Curve updated live.";
            }
            catch (ArgumentException ex)
            {
                StatusText = $"Invalid curve: {ex.Message}";
                return;
            }
        }

        PersistFanSettings();
    }

    /// <summary>
    /// Syncs the page (and persisted settings) with a fan configuration that was
    /// just applied through another path (profile apply, game rule, startup).
    /// </summary>
    public void SyncFromApplied(Core.Profiles.FanMode mode, uint fixedPct, FanCurveConfig? curve)
    {
        _loadingSettings = true;
        ModeIndex = mode switch
        {
            Core.Profiles.FanMode.Fixed => 1,
            Core.Profiles.FanMode.Curve => 2,
            _ => 0,
        };
        if (mode == Core.Profiles.FanMode.Fixed)
        {
            FixedDuty = fixedPct;
        }

        if (curve is not null)
        {
            Curve = curve;
            TempSourceIndex = (int)curve.TempSource;
            Hysteresis = curve.HysteresisC;
            ZeroRpmEnabled = curve.ZeroRpmBelowC > 0;
            if (curve.ZeroRpmBelowC > 0)
            {
                ZeroRpmBelow = curve.ZeroRpmBelowC;
            }
        }

        _loadingSettings = false;
        PersistFanSettings();
        StatusText = $"Fan mode set by profile: {mode}.";
    }

    /// <summary>Per-fan manual duty (advanced; NVAPI cooler ids are 1-based).</summary>
    [RelayCommand]
    private void SetSingleFan(string? parameter)
    {
        if (_gpu is null || parameter is null || !uint.TryParse(parameter, out uint coolerId))
        {
            return;
        }

        uint duty = Core.Tuning.TuningMath.NormalizeFixedFanDuty(
            (uint)FixedDuty, _gpu.Tuner.Capabilities.FanMinDutyPct);
        var rc = _gpu.Tuner.SetFanRaw(coolerId, duty);
        StatusText = rc == Core.Interop.Nvml.NvmlReturn.Success
            ? $"Fan {coolerId} set to {duty}% (others untouched)."
            : $"Per-fan command failed: {rc}";
    }
}
