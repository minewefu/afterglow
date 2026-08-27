using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Afterglow.Core;
using Afterglow.Core.Settings;
using Afterglow.Core.Telemetry;

namespace Afterglow.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PollingLabel))]
    private double _pollingIntervalMs;

    [ObservableProperty] private bool _csvLoggingEnabled;
    [ObservableProperty] private string _csvStatusText = string.Empty;

    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startMinimizedToTray;
    [ObservableProperty] private bool _resetOnDriverCrash;

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _startWithWindowsNote = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<AutomationRule> AutomationRules { get; } = [];

    [ObservableProperty] private int _newAutoMetricIndex;
    [ObservableProperty] private string _newAutoThreshold = "94";
    [ObservableProperty] private string _newAutoSeconds = "30";
    [ObservableProperty] private int _newAutoActionIndex;
    [ObservableProperty] private string _newAutoFanPct = "85";
    [ObservableProperty] private string _newAutoProfile = string.Empty;

    [RelayCommand]
    private void AddAutomationRule()
    {
        if (!double.TryParse(NewAutoThreshold, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double threshold) ||
            !int.TryParse(NewAutoSeconds, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int seconds))
        {
            return;
        }

        string action = NewAutoActionIndex switch { 1 => "profile", 2 => "reset", _ => "fans" };
        if (action == "profile" && (NewAutoProfile.Length == 0 || NewAutoProfile == "(none)"))
        {
            return;
        }

        uint fanPct = uint.TryParse(NewAutoFanPct, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out uint pct)
            ? Math.Clamp(pct, 30u, 100u)
            : 85u;

        AutomationRules.Add(new AutomationRule
        {
            Metric = NewAutoMetricIndex switch { 1 => "memjunction", 2 => "power", _ => "gpu" },
            Threshold = threshold,
            ForSeconds = Math.Clamp(seconds, 5, 3600),
            Action = action,
            ActionProfile = action == "profile" ? NewAutoProfile : null,
            ActionFanPct = fanPct,
        });
        PersistAutomationRules();
    }

    [RelayCommand]
    private void RemoveAutomationRule(AutomationRule? rule)
    {
        if (rule is not null && AutomationRules.Remove(rule))
        {
            PersistAutomationRules();
        }
    }

    private void PersistAutomationRules()
    {
        var rules = AutomationRules.ToArray();
        _services.UpdateSettings(s => s with { AutomationRules = rules });
    }

    [ObservableProperty] private double _alertGpuTemp;
    [ObservableProperty] private double _alertMemTemp;

    /// <summary>"(none)" + saved profile names for the startup-apply picker.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> StartupProfileOptions { get; } = [];

    [ObservableProperty] private string _selectedStartupProfile = "(none)";

    [ObservableProperty] private string _startupProfileNote = string.Empty;

    // Overlay
    [ObservableProperty] private int _overlayCornerIndex;
    [ObservableProperty] private double _overlayOpacity;
    [ObservableProperty] private bool _overlayShowFps;
    [ObservableProperty] private bool _overlayShowGraph;
    [ObservableProperty] private bool _overlayShowTemp;
    [ObservableProperty] private bool _overlayShowPower;
    [ObservableProperty] private bool _overlayShowClock;
    [ObservableProperty] private bool _overlayShowVram;
    [ObservableProperty] private bool _overlayShowFan;

    public string PollingLabel => $"{PollingIntervalMs:F0} ms";

    public string AlertGpuLabel => AlertGpuTemp < 50 ? "off" : $"{AlertGpuTemp:F0} °C";

    public string AlertMemLabel => AlertMemTemp < 50 ? "off" : $"{AlertMemTemp:F0} °C";

    public string HotkeysText { get; } =
        "Global hotkeys:  Ctrl+Alt+O toggle overlay · Ctrl+Alt+R panic reset to defaults · " +
        "Ctrl+Alt+1…5 apply saved profile 1…5 (alphabetical)";

    public string AboutText { get; } =
        $"Afterglow {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev"} — " +
        "open-source tuning and monitoring for NVIDIA RTX GPUs. MIT licensed. " +
        "No kernel drivers, no telemetry, no accounts.";

    public string DataPathText { get; } = $"Profiles, settings, and logs: {AppPaths.Root}";

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        RefreshStartupProfileOptions();
        LoadFrom(services.Settings);
    }

    /// <summary>Repopulates the startup-profile picker (called on page navigation too).</summary>
    public void RefreshStartupProfileOptions()
    {
        StartupProfileOptions.Clear();
        StartupProfileOptions.Add("(none)");
        foreach (var profile in _services.Profiles.LoadAll())
        {
            StartupProfileOptions.Add(profile.Name);
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        if (!_services.IsElevated)
        {
            StartWithWindowsNote = "Changing autostart needs the elevated app — restart Afterglow and accept the UAC prompt first.";
            RevertStartWithWindows(!value);
            return;
        }

        bool ok = value ? Services.StartupTaskService.Enable() : Services.StartupTaskService.Disable();
        if (!ok)
        {
            StartWithWindowsNote = value
                ? "Couldn't create the startup task (Task Scheduler refused)."
                : "Couldn't remove the startup task.";
            RevertStartWithWindows(!value);
            return;
        }

        StartWithWindowsNote = string.Empty;
    }

    private void RevertStartWithWindows(bool actual)
    {
        _loading = true;
        StartWithWindows = actual;
        _loading = false;
    }

    partial void OnSelectedStartupProfileChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        string? chosen = value == "(none)" ? null : value;
        if (chosen is not null)
        {
            var profile = _services.Profiles.Load(chosen);
            StartupProfileNote = profile is { MarkedStable: false }
                ? "This profile isn't marked stable yet — it won't actually apply at startup until you validate it (Stability page) and press 'Mark stable' in Profiles."
                : string.Empty;
        }
        else
        {
            StartupProfileNote = string.Empty;
        }

        _services.UpdateSettings(s => s with { ApplyProfileOnStart = chosen });
    }

    private void LoadFrom(AppSettings s)
    {
        _loading = true;
        PollingIntervalMs = s.PollingIntervalMs;
        StartWithWindows = Services.StartupTaskService.IsEnabled();
        AutomationRules.Clear();
        foreach (var rule in s.AutomationRules)
        {
            AutomationRules.Add(rule);
        }

        CloseToTray = s.CloseToTray;
        StartMinimizedToTray = s.StartMinimizedToTray;
        ResetOnDriverCrash = s.ResetOnDriverCrash;
        AlertGpuTemp = s.AlertGpuTempC == 0 ? 49 : s.AlertGpuTempC;
        AlertMemTemp = s.AlertMemJunctionTempC == 0 ? 49 : s.AlertMemJunctionTempC;
        OverlayCornerIndex = (int)s.Overlay.Corner;
        OverlayOpacity = s.Overlay.Opacity;
        OverlayShowFps = s.Overlay.ShowFps;
        OverlayShowGraph = s.Overlay.ShowFrametimeGraph;
        OverlayShowTemp = s.Overlay.ShowGpuTemp;
        OverlayShowPower = s.Overlay.ShowPower;
        OverlayShowClock = s.Overlay.ShowClock;
        OverlayShowVram = s.Overlay.ShowVram;
        OverlayShowFan = s.Overlay.ShowFan;
        SelectedStartupProfile = s.ApplyProfileOnStart is { } startup && StartupProfileOptions.Contains(startup)
            ? startup
            : "(none)";
        _loading = false;
    }

    private void Persist()
    {
        if (_loading)
        {
            return;
        }

        OnPropertyChanged(nameof(AlertGpuLabel));
        OnPropertyChanged(nameof(AlertMemLabel));
        _services.UpdateSettings(s => s with
        {
            PollingIntervalMs = (int)PollingIntervalMs,
            CloseToTray = CloseToTray,
            StartMinimizedToTray = StartMinimizedToTray,
            ResetOnDriverCrash = ResetOnDriverCrash,
            AlertGpuTempC = AlertGpuTemp < 50 ? 0 : (int)AlertGpuTemp,
            AlertMemJunctionTempC = AlertMemTemp < 50 ? 0 : (int)AlertMemTemp,
            Overlay = s.Overlay with
            {
                Corner = (OverlayCorner)OverlayCornerIndex,
                Opacity = OverlayOpacity,
                ShowFps = OverlayShowFps,
                ShowFrametimeGraph = OverlayShowGraph,
                ShowGpuTemp = OverlayShowTemp,
                ShowPower = OverlayShowPower,
                ShowClock = OverlayShowClock,
                ShowVram = OverlayShowVram,
                ShowFan = OverlayShowFan,
            },
        });
    }

    partial void OnPollingIntervalMsChanged(double value) => Persist();

    partial void OnCloseToTrayChanged(bool value) => Persist();

    partial void OnStartMinimizedToTrayChanged(bool value) => Persist();

    partial void OnResetOnDriverCrashChanged(bool value) => Persist();

    partial void OnAlertGpuTempChanged(double value) => Persist();

    partial void OnAlertMemTempChanged(double value) => Persist();

    partial void OnOverlayCornerIndexChanged(int value) => Persist();

    partial void OnOverlayOpacityChanged(double value) => Persist();

    partial void OnOverlayShowFpsChanged(bool value) => Persist();

    partial void OnOverlayShowGraphChanged(bool value) => Persist();

    partial void OnOverlayShowTempChanged(bool value) => Persist();

    partial void OnOverlayShowPowerChanged(bool value) => Persist();

    partial void OnOverlayShowClockChanged(bool value) => Persist();

    partial void OnOverlayShowVramChanged(bool value) => Persist();

    partial void OnOverlayShowFanChanged(bool value) => Persist();

    partial void OnCsvLoggingEnabledChanged(bool value)
    {
        if (value)
        {
            var logger = new CsvLogger();
            logger.Start();
            _services.ActiveCsvLogger = logger;
            _services.Telemetry.SnapshotTaken += logger.Log;
            CsvStatusText = $"Logging to {logger.CurrentFile}";
        }
        else if (_services.ActiveCsvLogger is { } active)
        {
            _services.Telemetry.SnapshotTaken -= active.Log;
            active.Dispose();
            _services.ActiveCsvLogger = null;
            CsvStatusText = "Logging stopped.";
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            AppPaths.EnsureCreated();
            _ = System.Diagnostics.Process.Start("explorer.exe", AppPaths.Root);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            CsvStatusText = $"Could not open folder: {ex.Message}";
        }
    }
}
