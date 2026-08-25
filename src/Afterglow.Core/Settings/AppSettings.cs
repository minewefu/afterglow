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

    /// <summary>Fan mode/curve, restored automatically at startup (elevated).</summary>
    public FanSettings Fans { get; init; } = new();

    public IReadOnlyList<GameRule> GameRules { get; init; } = [];

    /// <summary>Alert: flash tray + notification above this GPU temperature (0 = off).</summary>
    public int AlertGpuTempC { get; init; }

    /// <summary>Alert above this hot-spot/memory-junction temperature (0 = off).</summary>
    public int AlertMemJunctionTempC { get; init; }
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
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(AppPaths.SettingsFile), JsonOptions) ?? new AppSettings();
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
            string temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            if (File.Exists(AppPaths.SettingsFile))
            {
                File.Replace(temp, AppPaths.SettingsFile, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, AppPaths.SettingsFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings persistence must never crash the app.
        }
    }
}
