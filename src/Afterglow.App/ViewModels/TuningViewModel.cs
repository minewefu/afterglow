using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core.Hardware;
using Afterglow.Core.Profiles;
using Afterglow.Core.Tuning;

namespace Afterglow.App.ViewModels;

public partial class TuningViewModel : ObservableObject
{
    private readonly AppServices _services;
    private GpuContext? _gpu;

    public TuningCapabilities Capabilities { get; private set; }

    // Pending values (sliders)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoreOffsetLabel))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private double _coreOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemOffsetLabel))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private double _memOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerLimitLabel))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private double _powerLimit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoltageBoostLabel))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private double _voltageBoost;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _lockEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockClockLabel))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private double _lockClock = 2500;

    [ObservableProperty]
    private string _applyResultText = string.Empty;

    [ObservableProperty]
    private bool _lastApplyFailed;

    public string CoreOffsetLabel => $"{(CoreOffset >= 0 ? "+" : string.Empty)}{CoreOffset:F0} MHz";

    public string MemOffsetLabel => $"{(MemOffset >= 0 ? "+" : string.Empty)}{MemOffset:F0} MHz";

    public string PowerLimitLabel => Capabilities.PowerLimitDefaultW > 0
        ? $"{PowerLimit:F0} W  ({PowerLimit / Capabilities.PowerLimitDefaultW * 100:F0}%)"
        : $"{PowerLimit:F0} W";

    public string VoltageBoostLabel => $"+{VoltageBoost:F0}%";

    public string LockClockLabel => $"{LockClock:F0} MHz";

    // The capability term applies only to non-NVIDIA GPUs: the NVIDIA gate is
    // deliberately unchanged (it cannot be regression-tested on this machine),
    // including its behavior on cards where individual probes failed.
    public bool CanTune => _services.DemoMode
        || (_services.IsElevated && _gpu is not null
            && (_gpu.Vendor == Core.Hardware.GpuVendor.Nvidia || HasAnyTuningKnob(Capabilities)));

    public string TuneGateText => CanTune
        ? string.Empty
        : _gpu is null
            ? "No supported GPU detected — tuning unavailable."
            : _gpu.Vendor != Core.Hardware.GpuVendor.Nvidia && !HasAnyTuningKnob(Capabilities)
                ? "Tuning isn't implemented for this GPU yet — monitoring only in this beta."
                : "Running without administrator rights — monitoring works, tuning is locked. Restart Afterglow and accept the elevation prompt to tune.";

    private static bool HasAnyTuningKnob(TuningCapabilities c) =>
        c.SupportsCoreOffset || c.SupportsMemOffset || c.SupportsPowerLimit
        || c.SupportsLockedCoreClock || c.SupportsVoltageBoost || c.SupportsTempLimit
        || c.SupportsVfPoints;

    public bool SupportsTempLimit => Capabilities.SupportsTempLimit;

    public string TempLimitNote => Capabilities.SupportsTempLimit
        ? string.Empty
        : IsNvidiaOrDemo
            ? "Temperature-limit control isn't exposed by this driver generation; use the power limit and fan curve instead."
            : "Temperature-limit control isn't exposed by this device's driver; use the clock clamp instead.";

    // Everything below renders the NVIDIA page exactly as it has always been
    // (all true / historical strings); on Intel each card appears only when
    // Afterglow actually drives that knob on this device.
    private bool IsNvidiaOrDemo =>
        _services.DemoMode || _gpu is null || _gpu.Vendor == Core.Hardware.GpuVendor.Nvidia;

    public bool ShowCoreOffset => IsNvidiaOrDemo || Capabilities.SupportsCoreOffset;

    public bool ShowMemOffset => IsNvidiaOrDemo || Capabilities.SupportsMemOffset;

    public bool ShowPowerLimit => IsNvidiaOrDemo || Capabilities.SupportsPowerLimit;

    public bool ShowClockLock => IsNvidiaOrDemo || Capabilities.SupportsLockedCoreClock;

    /// <summary>The wizard's lock+offset trade needs both knobs.</summary>
    public bool ShowUndervoltWizard => IsNvidiaOrDemo
        || (Capabilities.SupportsCoreOffset && Capabilities.SupportsLockedCoreClock);

    public double LockClockSliderMin =>
        !IsNvidiaOrDemo && Capabilities.LockClockMinMHz > 0 ? Capabilities.LockClockMinMHz : 1000;

    public string PageSubtitle => IsNvidiaOrDemo
        ? "Every range below comes from the driver for this exact GPU. Values are clamped and applied knob-by-knob; offsets, power limit, and voltage boost are verified by reading back (the driver offers no getter for the clock lock — Afterglow tracks it)."
        : "Every range below comes from the driver for this exact GPU. Values are clamped and applied knob-by-knob, and the frequency clamp is verified by reading it back from the driver.";

    public string ClockLockTitle => IsNvidiaOrDemo ? "Clock lock (undervolt)" : "Clock clamp";

    public string ClockLockDescription => IsNvidiaOrDemo
        ? "Caps boost at a target clock while a positive core offset shifts the V/F curve — the GPU reaches the target at a lower voltage. The documented-API undervolt for RTX 50."
        : "Clamps the GPU's frequency range below its stock maximum — the driver-supported lever on this device for trading peak clocks against power and heat. Applies and releases are verified by reading the range back from the driver.";

    public string PowerLimitNote => !IsNvidiaOrDemo && !Capabilities.SupportsPowerLimit
        ? "This device's driver exposes no power-limit control (probed live via IGCL). The clock clamp is the driver-supported lever for power and heat — and the package power budget is shared with the CPU either way."
        : string.Empty;

    // Demo-mode ranges mirror an RTX 5090 so the whole UI is explorable.
    private static readonly TuningCapabilities DemoCapabilities = new()
    {
        SupportsCoreOffset = true,
        CoreOffsetMinMHz = -1000,
        CoreOffsetMaxMHz = 1000,
        SupportsMemOffset = true,
        MemOffsetMinMHz = -2000,
        MemOffsetMaxMHz = 6000,
        SupportsPowerLimit = true,
        PowerLimitMinW = 400,
        PowerLimitMaxW = 575,
        PowerLimitDefaultW = 575,
        SupportsLockedCoreClock = true,
        MaxCoreClockMHz = 3090,
        SupportsFanControl = true,
        FanCount = 3,
        FanMinDutyPct = 30,
        SupportsVoltageBoost = true,
    };

    public TuningViewModel(AppServices services)
    {
        _services = services;
        _gpu = services.SelectedGpu;
        Capabilities = _gpu?.Tuner.Capabilities ?? DemoCapabilities;
        RefreshFromHardware();
    }

    /// <summary>The UI moved to another GPU: re-read its ranges and applied values.</summary>
    public void RebindGpu()
    {
        _gpu = _services.SelectedGpu;
        Capabilities = _gpu?.Tuner.Capabilities ?? DemoCapabilities;
        OnPropertyChanged(nameof(Capabilities));
        OnPropertyChanged(nameof(CanTune));
        OnPropertyChanged(nameof(TuneGateText));
        OnPropertyChanged(nameof(SupportsTempLimit));
        OnPropertyChanged(nameof(TempLimitNote));
        OnPropertyChanged(nameof(ShowCoreOffset));
        OnPropertyChanged(nameof(ShowMemOffset));
        OnPropertyChanged(nameof(ShowPowerLimit));
        OnPropertyChanged(nameof(ShowClockLock));
        OnPropertyChanged(nameof(ShowUndervoltWizard));
        OnPropertyChanged(nameof(LockClockSliderMin));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(ClockLockTitle));
        OnPropertyChanged(nameof(ClockLockDescription));
        OnPropertyChanged(nameof(PowerLimitNote));
        RefreshFromHardware();
    }

    /// <summary>Loads the currently applied values into the sliders.</summary>
    public void RefreshFromHardware()
    {
        if (_gpu is null)
        {
            PowerLimit = Capabilities.PowerLimitDefaultW;
            return;
        }

        var (core, mem, power, boost, lockMHz) = _gpu.Tuner.ReadCurrent();
        CoreOffset = core;
        MemOffset = mem;
        PowerLimit = power is > 0 ? power.Value : Capabilities.PowerLimitDefaultW;
        VoltageBoost = boost ?? 0;
        LockEnabled = lockMHz is not null;
        if (lockMHz is uint lc)
        {
            LockClock = lc;
            _lockClockBeforeCoercion = null;
        }
        else if (!IsNvidiaOrDemo && Capabilities.MaxCoreClockMHz > 0 && LockClock > Capabilities.MaxCoreClockMHz)
        {
            // The default slider position (2500) can exceed an iGPU's whole
            // range; start at the device's stock maximum instead — and
            // remember the prior value so switching back to a GPU that can
            // hold it doesn't inherit the iGPU's ceiling.
            _lockClockBeforeCoercion ??= LockClock;
            LockClock = Capabilities.MaxCoreClockMHz;
        }
        else if (_lockClockBeforeCoercion is double prior
            && (IsNvidiaOrDemo || Capabilities.MaxCoreClockMHz == 0 || prior <= Capabilities.MaxCoreClockMHz))
        {
            LockClock = prior;
            _lockClockBeforeCoercion = null;
        }
    }

    private double? _lockClockBeforeCoercion;

    /// <summary>The pending values as a profile (used by Apply and by "save as profile").</summary>
    public TuningProfile ToProfile(string name) => new()
    {
        Name = name,
        CoreOffsetMHz = (int)CoreOffset,
        MemOffsetMHz = (int)MemOffset,
        PowerLimitW = Capabilities.SupportsPowerLimit ? PowerLimit : null,
        VoltageBoostPct = Capabilities.SupportsVoltageBoost ? (uint)VoltageBoost : null,
        LockedCoreClockMHz = LockEnabled ? (uint)LockClock : null,
    };

    /// <summary>Loads a profile's values into the sliders (does not apply).</summary>
    public void LoadProfile(TuningProfile profile)
    {
        CoreOffset = profile.CoreOffsetMHz;
        MemOffset = profile.MemOffsetMHz;
        if (profile.PowerLimitW is double p)
        {
            PowerLimit = p;
        }

        VoltageBoost = profile.VoltageBoostPct ?? 0;
        LockEnabled = profile.LockedCoreClockMHz is not null;
        if (profile.LockedCoreClockMHz is uint lc)
        {
            LockClock = lc;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTune))]
    private void Apply()
    {
        if (_gpu is null)
        {
            ApplyResultText = "Applied (demo mode — no hardware was changed).";
            LastApplyFailed = false;
            return;
        }

        var result = _gpu.Tuner.Apply(ToProfile("Current"));
        ApplyResultText = result.Summary;
        LastApplyFailed = !result.AllSucceeded;
        RefreshFromHardware();
    }

    /// <summary>
    /// Undervolt presets via the lock+offset method: cap the boost clock while a
    /// positive offset shifts the V/F curve, so the target clock is reached at a
    /// lower voltage. Values load into the sliders; Apply commits them.
    /// </summary>
    [RelayCommand]
    private void UndervoltPreset(string level)
    {
        (uint lockClock, int offset) = level switch
        {
            "efficiency" => (2500u, 150),
            "balanced" => (2650u, 125),
            _ => (2800u, 100),
        };

        LockEnabled = true;
        LockClock = Math.Min(lockClock, Capabilities.MaxCoreClockMHz);
        CoreOffset = Math.Clamp(offset, Capabilities.CoreOffsetMinMHz, Capabilities.CoreOffsetMaxMHz);
        ApplyResultText =
            $"Undervolt preset loaded: cap {LockClock:F0} MHz with +{CoreOffset:F0} MHz offset. " +
            "Press Apply, then watch core voltage under load on the Dashboard — validate stability with the Stability page.";
        LastApplyFailed = false;
    }

    [RelayCommand]
    private void Reset()
    {
        if (_gpu is null)
        {
            CoreOffset = 0;
            MemOffset = 0;
            PowerLimit = Capabilities.PowerLimitDefaultW;
            VoltageBoost = 0;
            LockEnabled = false;
            ApplyResultText = "Reset (demo mode).";
            LastApplyFailed = false;
            return;
        }

        var result = _gpu.Tuner.ResetToDefaults();
        ApplyResultText = result.Summary;
        LastApplyFailed = !result.AllSucceeded;
        LockEnabled = false;
        RefreshFromHardware();
    }
}
