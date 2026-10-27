using System.Text.Json;
using System.Text.Json.Serialization;
using Afterglow.Core.Fans;

namespace Afterglow.Core.Settings;

/// <summary>Persisted fan control configuration (restored at startup when elevated).</summary>
public sealed record FanSettings
{
    /// <summary>"auto" | "fixed" | "curve".</summary>
    public string Mode { get; init; } = "auto";

    public uint FixedDutyPct { get; init; } = 50;

    public FanCurveConfig Curve { get; init; } = new();
}

/// <summary>Where the overlay docks.</summary>
public enum OverlayCorner
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
}

public sealed record OverlaySettings
{
    public bool Enabled { get; init; }

    public OverlayCorner Corner { get; init; } = OverlayCorner.TopLeft;

    public double Opacity { get; init; } = 0.85;

    public bool ShowFps { get; init; } = true;

    public bool ShowFrametimeGraph { get; init; } = true;

    public bool ShowGpuTemp { get; init; } = true;

    public bool ShowHotSpot { get; init; }

    public bool ShowPower { get; init; } = true;

    public bool ShowClock { get; init; } = true;

    public bool ShowVram { get; init; }

    public bool ShowFan { get; init; }

    public bool ShowThrottle { get; init; } = true;

    public double FontSize { get; init; } = 13;
}

/// <summary>Auto-apply a profile when a given executable runs.</summary>
public sealed record GameRule
{
    /// <summary>Executable name without path, e.g. "cyberpunk2077.exe".</summary>
    public required string ExecutableName { get; init; }

    public required string ProfileName { get; init; }

    /// <summary>Restore the previous tuning when the game exits.</summary>
    public bool RevertOnExit { get; init; } = true;

    /// <summary>Driver frame-rate limiter for this game (DRS); 0 = off.</summary>
    public int FrameCapFps { get; init; }

    /// <summary>Driver vsync for this game: "default", "on", or "off".</summary>
    public string DriverVsync { get; init; } = "default";

    /// <summary>Cap the game's pre-rendered frames at 1 (driver low-latency).</summary>
    public bool LowLatency { get; init; }
}

/// <summary>
/// Global keyboard shortcuts. Each is null for the default (see
/// <see cref="Hotkeys.HotkeyDefaults"/>), an empty string for none, or the user's
/// own "Ctrl+Alt+O"-style text. Settings files from before shortcuts were
/// configurable have no such section, so everything is the default. Whatever is
/// set, a shortcut that types a character on one of the user's keyboard layouts
/// is never registered (<see cref="Hotkeys.HotkeyPlanner"/>).
/// </summary>
public sealed record HotkeySettings
{
    /// <summary>Off: Afterglow registers no global shortcut at all.</summary>
    public bool Enabled { get; init; } = true;

    public string? ToggleOverlay { get; init; }

    public string? PanicReset { get; init; }

    /// <summary>Profile slots 1 to 5 (saved profiles in alphabetical order); missing entries are the default.</summary>
    public IReadOnlyList<string?> ProfileSlots { get; init; } = [];

    /// <summary>The user's text for <paramref name="action"/>: null for the default, empty for none.</summary>
    public string? For(Hotkeys.HotkeyAction action)
    {
        string? text = action switch
        {
            Hotkeys.HotkeyAction.ToggleOverlay => ToggleOverlay,
            Hotkeys.HotkeyAction.PanicReset => PanicReset,
            _ => Hotkeys.HotkeyActions.SlotOf(action) is int slot && ProfileSlots is { } slots && slot < slots.Count
                ? slots[slot]
                : null,
        };
        return text?.Trim();
    }

    /// <param name="text">Null for the default, empty for none, otherwise the shortcut.</param>
    public HotkeySettings With(Hotkeys.HotkeyAction action, string? text)
    {
        switch (action)
        {
            case Hotkeys.HotkeyAction.ToggleOverlay:
                return this with { ToggleOverlay = text };
            case Hotkeys.HotkeyAction.PanicReset:
                return this with { PanicReset = text };
            default:
                var slots = Enumerable.Range(0, Hotkeys.HotkeyActions.ProfileSlotCount)
                    .Select(i => For(Hotkeys.HotkeyActions.ProfileSlot(i)))
                    .ToArray();
                slots[Hotkeys.HotkeyActions.SlotOf(action)!.Value] = text;
                return this with { ProfileSlots = slots };
        }
    }
}

/// <summary>Sustained-condition automation: when a metric breaches for long enough, act.</summary>
public sealed record AutomationRule
{
    /// <summary>"gpu" (core temp), "memjunction", or "power".</summary>
    public required string Metric { get; init; }

    public required double Threshold { get; init; }

    /// <summary>The breach must persist this long before the action fires.</summary>
    public int ForSeconds { get; init; } = 30;

    /// <summary>"profile" (apply ActionProfile), "fans" (fixed ActionFanPct), or "reset".</summary>
    public required string Action { get; init; }

    public string? ActionProfile { get; init; }

    public uint ActionFanPct { get; init; } = 80;
}

public sealed record AppSettings
{
    public int PollingIntervalMs { get; init; } = 1000;

    public bool CloseToTray { get; init; }

    public bool StartMinimizedToTray { get; init; }

    /// <summary>Profile applied automatically when Afterglow starts (only if marked stable or confirmed).</summary>
    public string? ApplyProfileOnStart { get; init; }

    /// <summary>Reset all tuning to driver defaults if a GPU driver TDR/crash is detected.</summary>
    public bool ResetOnDriverCrash { get; init; } = true;

    public OverlaySettings Overlay { get; init; } = new();

    public HotkeySettings Hotkeys { get; init; } = new();

    /// <summary>
    /// Fan mode/curve for the primary GPU, restored automatically at startup
    /// (elevated). Kept as the legacy single-GPU field; secondary cards live
    /// in <see cref="FansByGpu"/>.
    /// </summary>
    public FanSettings Fans { get; init; } = new();

    /// <summary>Per-GPU fan configuration, keyed by GPU UUID (or "index:N" when the driver reports none).</summary>
    public IReadOnlyDictionary<string, FanSettings> FansByGpu { get; init; } =
        new Dictionary<string, FanSettings>();

    public IReadOnlyList<GameRule> GameRules { get; init; } = [];

    /// <summary>Alert: flash tray + notification above this GPU temperature (0 = off).</summary>
    public int AlertGpuTempC { get; init; }

    /// <summary>Alert above this hot-spot/memory-junction temperature (0 = off).</summary>
    public int AlertMemJunctionTempC { get; init; }

    /// <summary>Sustained-condition automation rules (temperature/power watchdogs with actions).</summary>
    public IReadOnlyList<AutomationRule> AutomationRules { get; init; } = [];

    /// <summary>
    /// Opt-in (default off): at startup, one anonymous request to the GitHub
    /// Releases API to see whether a newer version exists. Nothing is uploaded.
    /// </summary>
    public bool UpdateCheckEnabled { get; init; }
}

/// <summary>Atomic JSON persistence for <see cref="AppSettings"/>.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        // First read in the elevated app: lock down the data root and turn on
        // the load-time owner check, so settings planted by a standard user are
        // not trusted (ProgramData hardening). No-op when unelevated or --data-dir.
        Security.DataRootProtection.EnsureHardened();

        try
        {
            // Null when missing or — elevated — owned by an untrusted principal
            // (planted before the lock-down): safe defaults either way. The owner
            // is checked on the same open handle the content is read from.
            string? json = Security.DataRootProtection.TryReadAllText(AppPaths.SettingsFile);
            if (json is not null)
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

                // A hand-edited "hotkeys": null means the defaults, not a crash.
                return settings.Hotkeys is null ? settings with { Hotkeys = new HotkeySettings() } : settings;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt settings fall back to defaults; the bad file is preserved as .bad for inspection.
            try
            {
                File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bad", overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureCreated();
            Security.DataRootProtection.WriteAllTextAtomic(
                AppPaths.SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings persistence must never crash the app.
        }
    }
}
