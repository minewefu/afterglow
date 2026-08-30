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
    private GpuContext? _gpu;
    private FanControlService? _fanControl;

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

    public bool HasMemJunction { get; private set; }

    public FansViewModel(AppServices services)
    {
        _services = services;
        BindGpu(services.SelectedGpu);

        // Restore the selected card's persisted configuration into the editor.
        LoadFanSettings(_gpu is { } gpu ? services.FanSettingsFor(gpu) : services.Settings.Fans);

        services.Telemetry.SnapshotTaken += OnSnapshot;
    }

    private void BindGpu(GpuContext? gpu)
    {
        _gpu = gpu;
        _fanControl = null;
        if (_gpu is not null)
        {
            _services.FanControl.TryGetValue(_gpu.Index, out var fans);
            _fanControl = fans;
        }

        HasMemJunction = _services.DemoMode ||
            (_gpu?.Nvapi?.GetPrivateThermals().MemJunctionC is not null);

        if (_gpu?.Nvapi is not null &&
            _gpu.Nvapi.TryGetFanStatus(out var fanInfos) == Core.Interop.Nvapi.NvapiStatus.Ok)
        {
            FanIds = fanInfos.Select(f => f.CoolerId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        }
        else
        {
            FanIds = _services.DemoMode ? ["1", "2", "3"] : [];
        }
    }

    /// <summary>
    /// The UI moved to another GPU: rebind the fan service and per-fan
    /// readouts, and load that card's own persisted fan configuration into
    /// the editor.
    /// </summary>
    public void RebindGpu()
    {
        BindGpu(_services.SelectedGpu);
        OnPropertyChanged(nameof(HasMemJunction));
        OnPropertyChanged(nameof(FanIds));
        OnPropertyChanged(nameof(CanControl));
        OnPropertyChanged(nameof(GateText));
        LoadFanSettings(_gpu is { } gpu ? _services.FanSettingsFor(gpu) : _services.Settings.Fans);
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
        var settings = new Core.Settings.FanSettings
        {
            Mode = mode switch
            {
                Core.Profiles.FanMode.Fixed => "fixed",
                Core.Profiles.FanMode.Curve => "curve",
                _ => "auto",
            },
            FixedDutyPct = fixedPct,
            Curve = curve,
        };

        // The edit belongs to the selected card. The primary GPU also keeps
        // the legacy single-GPU field in sync so a downgrade reads it.
        var gpu = _gpu;
        _services.UpdateSettings(s =>
        {
            if (gpu is null)
            {
                return s with { Fans = settings };
            }

            var byGpu = new Dictionary<string, Core.Settings.FanSettings>(
                s.FansByGpu, StringComparer.Ordinal)
            {
                [AppServices.FanKeyFor(gpu)] = settings,
            };
            bool isPrimary = _services.Gpus.Count > 0 && gpu.Index == _services.Gpus[0].Index;
            return s with { FansByGpu = byGpu, Fans = isPrimary ? settings : s.Fans };
        });
    }

    /// <summary>Loads one GPU's persisted fan configuration into the editor.</summary>
    private void LoadFanSettings(Core.Settings.FanSettings saved)
    {
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
                    bool pinned = _fanControl.SetFixed((uint)FixedDuty);
                    StatusText = !pinned
                        ? $"The driver did not accept fixed {duty}% — the fans are unchanged."
                        : duty == (uint)FixedDuty
                            ? $"Fixed {duty}% applied to all fans."
                            : $"Fixed {duty}% applied (requested {FixedDuty:F0}%, raised to the hardware minimum spin duty).";
                    break;
                case 2:
                    _fanControl.SetCurve(BuildConfig());
                    StatusText = "Curve active — Afterglow is driving the fans.";
                    break;
                default:
                    // Only claim the restore when the driver actually granted it.
                    StatusText = _fanControl.SetAuto()
                        ? "Firmware (auto) fan control restored."
                        : "The driver did not accept the release, so the fans stay as they are. If they are pinned, " +
                          "this usually means Afterglow is not running as administrator; on a card with no fan " +
                          "control there was nothing to release.";
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
