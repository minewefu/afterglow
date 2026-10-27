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
    [NotifyPropertyChangedFor(nameof(CoreOffsetLabel), nameof(HasPendingChanges))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial double CoreOffset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemOffsetLabel), nameof(HasPendingChanges))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial double MemOffset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerLimitLabel), nameof(HasPendingChanges))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial double PowerLimit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoltageBoostLabel), nameof(HasPendingChanges))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial double VoltageBoost { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool LockEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockClockLabel), nameof(HasPendingChanges))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial double LockClock { get; set; } = 2500;

    [ObservableProperty]
    public partial string ApplyResultText { get; set; } = string.Empty;

    /// <summary>
    /// Set when the card's record holds a clock lock applied before the system
    /// last slept or hibernated: not shown as applied, but it may still be on
    /// the card (see <see cref="IGpuTuner.UnconfirmedLockMHz"/>). Empty otherwise.
    /// </summary>
    [ObservableProperty]
    public partial string ClockLockNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool LastApplyFailed { get; set; }

    public string CoreOffsetLabel => $"{(CoreOffset >= 0 ? "+" : string.Empty)}{CoreOffset:F0} MHz";

    public string MemOffsetLabel => $"{(MemOffset >= 0 ? "+" : string.Empty)}{MemOffset:F0} MHz";

    public string PowerLimitLabel => Capabilities.PowerLimitDefaultW > 0
        ? $"{PowerLimit:F0} W  ({PowerLimit / Capabilities.PowerLimitDefaultW * 100:F0}%)"
        : $"{PowerLimit:F0} W";

    public string VoltageBoostLabel => $"+{VoltageBoost:F0}%";

    // What the sliders showed when they were last loaded from the card (or applied in
    // demo mode): anything else on them is a change Apply hasn't sent yet.
    private (double Core, double Mem, double Power, double Boost, bool Lock, double LockClock) _shownState;

    private (double, double, double, double, bool, double) PendingState =>
        (CoreOffset, MemOffset, PowerLimit, VoltageBoost, LockEnabled, LockEnabled ? LockClock : 0);

    /// <summary>The sliders hold values the card isn't running yet (shown by the Apply bar).</summary>
    public bool HasPendingChanges => PendingState != _shownState;

    private void MarkShown()
    {
        _shownState = PendingState;
        OnPropertyChanged(nameof(HasPendingChanges));
    }

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

    /// <summary>The clocks-and-power card, hidden when a device has none of its knobs (an Arc iGPU with only the clamp).</summary>
    public bool ShowClocksCard =>
        ShowCoreOffset || ShowMemOffset || ShowPowerLimit || Capabilities.SupportsVoltageBoost;

    /// <summary>The wizard's lock+offset trade needs both knobs.</summary>
    public bool ShowUndervoltWizard => IsNvidiaOrDemo
        || (Capabilities.SupportsCoreOffset && Capabilities.SupportsLockedCoreClock);

    public double LockClockSliderMin =>
        !IsNvidiaOrDemo && Capabilities.LockClockMinMHz > 0 ? Capabilities.LockClockMinMHz : 1000;

    public string PageSubtitle => IsNvidiaOrDemo
        ? "Every range comes from the driver for this exact GPU. Changes take effect when you press Apply, and each one is read back to confirm it landed, except the clock lock: the driver can't report it, so Afterglow keeps track of it."
        : "Every range comes from the driver for this exact GPU. Changes take effect when you press Apply, and the frequency clamp is read back from the driver to confirm it landed.";

    public string ClockLockTitle => IsNvidiaOrDemo ? "Clock lock (undervolt)" : "Clock clamp";

    public string ClockLockDescription => IsNvidiaOrDemo
        ? "Caps boost at a target clock while a positive core offset shifts the V/F curve, so the GPU reaches that clock at a lower voltage. The documented-API way to undervolt RTX 50."
        : "Holds the GPU's frequency range below its stock maximum: the lever this driver offers for trading peak clocks against power and heat. Each apply and release is read back from the driver.";

    public string PowerLimitNote => !IsNvidiaOrDemo && !Capabilities.SupportsPowerLimit
        ? "This driver offers no power-limit control (checked live through IGCL). Use the clock clamp for power and heat; the package power budget is shared with the CPU either way."
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
        WatchTuner();
        RefreshFromHardware();
    }

    /// <summary>The UI moved to another GPU: re-read its ranges and applied values.</summary>
    public void RebindGpu()
    {
        _gpu = _services.SelectedGpu;

        // "Applied: core offset +150 MHz" is a statement about the card it was
        // written to. RefreshFromHardware below re-reads the NEW card's sliders,
        // so leaving this line standing put the previous card's apply result
        // over the new card's freshly read values.
        ApplyResultText = string.Empty;
        LastApplyFailed = false;

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
        OnPropertyChanged(nameof(ShowClocksCard));
        OnPropertyChanged(nameof(ShowUndervoltWizard));
        OnPropertyChanged(nameof(LockClockSliderMin));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(ClockLockTitle));
        OnPropertyChanged(nameof(ClockLockDescription));
        OnPropertyChanged(nameof(PowerLimitNote));
        WatchTuner();
        RefreshFromHardware();
    }

    // --- Writes made elsewhere (F-75) ---------------------------------------
    // The V/F page, the stepper, the certifier, profile hotkeys and resets all
    // write through the same tuner; without this the sliders kept showing the
    // pre-change offset and lock, and "Save current" on the Profiles page then
    // persisted values the card was not running.

    private IGpuTuner? _watchedTuner;

    /// <summary>The applied values the sliders were last loaded from.</summary>
    private (int Core, int Mem, double? Power, uint? Boost, uint? Lock)? _lastRead;

    private void WatchTuner()
    {
        var tuner = _gpu?.Tuner;
        if (ReferenceEquals(tuner, _watchedTuner))
        {
            return;
        }

        if (_watchedTuner is not null)
        {
            _watchedTuner.StateWritten -= OnTunerStateWritten;
        }

        _watchedTuner = tuner;
        if (tuner is not null)
        {
            tuner.StateWritten += OnTunerStateWritten;
        }
    }

    // Raised on the writing thread (possibly a stepper or probe thread), after
    // the tuner released its lock: hop to the UI thread without blocking it.
    private void OnTunerStateWritten(IGpuTuner tuner) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_gpu?.Tuner, tuner) || _gpu is null)
            {
                return; // the selection moved to another card meanwhile
            }

            // Only resync when what this page shows actually changed: a per-point
            // edit leaves the offsets and lock alone, and must not throw away
            // slider values the user is still composing.
            if (_gpu.Tuner.ReadCurrent() == _lastRead)
            {
                return;
            }

            RefreshFromHardware();
            ApplyResultText = "Applied values changed elsewhere (another page, a profile, a reset or a " +
                              "stability run) — the sliders now show what the card is running.";
            LastApplyFailed = false;
        });

    /// <summary>Loads the currently applied values into the sliders.</summary>
    public void RefreshFromHardware()
    {
        if (_gpu is null)
        {
            PowerLimit = Capabilities.PowerLimitDefaultW;
            MarkShown();
            return;
        }

        var current = _gpu.Tuner.ReadCurrent();
        _lastRead = current;
        var (core, mem, power, boost, _) = current;
        CoreOffset = core;
        MemOffset = mem;
        PowerLimit = power is > 0 ? power.Value : Capabilities.PowerLimitDefaultW;
        VoltageBoost = boost ?? 0;
        // The lock box reflects the lock Afterglow APPLIED, not ReadCurrent's
        // observed element: on Arc a factory ceiling below the domain maximum
        // reads as a lock there, and ticking the box with it made the next
        // Apply write the ceiling back as a clamp with written provenance.
        uint? appliedLock = _gpu.Tuner.AppliedLockMHz;
        ClockLockNote = appliedLock is null && _gpu.Tuner.UnconfirmedLockMHz is uint maybe
            ? GpuTuner.DescribeUnconfirmedLock(maybe)
            : string.Empty;
        LockEnabled = appliedLock is not null;
        if (appliedLock is uint lc)
        {
            LockClock = lc;
            _lockClockBeforeCoercion = null;
        }
        else if (!IsNvidiaOrDemo && Capabilities.MaxCoreClockMHz > 0 && LockClock > Capabilities.MaxCoreClockMHz)
        {
            // The default slider position (2500) can exceed an iGPU's whole
            // range; start just below the device's stock maximum instead — and
            // remember the prior value so switching back to a GPU that can
            // hold it doesn't inherit the iGPU's ceiling.
            //
            // Just below, not at: a clamp AT the maximum is the unrestricted
            // range, which the engine refuses as a no-op. Landing exactly there
            // meant ticking "Lock clock" and pressing Apply without touching the
            // slider always failed.
            _lockClockBeforeCoercion ??= LockClock;
            LockClock = Math.Max(
                Capabilities.LockClockMinMHz,
                Capabilities.MaxCoreClockMHz - LockClockCeilingMarginMHz);
        }
        else if (_lockClockBeforeCoercion is double prior
            && (IsNvidiaOrDemo || Capabilities.MaxCoreClockMHz == 0 || prior <= Capabilities.MaxCoreClockMHz))
        {
            LockClock = prior;
            _lockClockBeforeCoercion = null;
        }

        MarkShown();
    }

    /// <summary>
    /// How far below the device maximum the lock slider is parked when the
    /// current value exceeds the device's range. A clamp at the maximum is the
    /// unrestricted range and is refused as a no-op, so the default position
    /// must sit below it.
    /// </summary>
    private const uint LockClockCeilingMarginMHz = 50;

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
            MarkShown();
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
            MarkShown();
            return;
        }

        // The card's fan service is reset with the tuner, and first (R-09): the
        // tuner's reset hands the fans back to firmware at the driver, but a fan
        // service left in Curve mode commanded them again on the next telemetry
        // tick — with this page saying "fans: ok (auto)".
        _services.FanControl.TryGetValue(_gpu.Index, out var fans);
        var reset = DefaultsReset.Card(_gpu.Index, _gpu.Tuner, fans);
        var result = reset.AsApplyResult();
        ApplyResultText = result.Summary;
        LastApplyFailed = !result.AllSucceeded;
        LockEnabled = false;
        RefreshFromHardware();
        ResetCompleted?.Invoke(reset);
    }

    /// <summary>Raised after Reset, so the Fans page can say the fans went back to firmware control.</summary>
    public event Action<CardResetResult>? ResetCompleted;
}
