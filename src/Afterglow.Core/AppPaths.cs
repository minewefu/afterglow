namespace Afterglow.Core;

/// <summary>
/// Canonical file-system locations. Everything lives under ProgramData so the
/// elevated app, the CLI, and a login task all see the same state.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Afterglow");

    public static string ProfilesDir => Path.Combine(Root, "profiles");

    public static string LogsDir => Path.Combine(Root, "logs");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Written while tuning values are applied; consulted on startup for crash recovery.</summary>
    public static string AppliedStateFile => Path.Combine(Root, "applied-state.json");

    /// <summary>Flight-recorder telemetry ring and crash-forensics reports.</summary>
    public static string FlightDir => Path.Combine(Root, "flight");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(LogsDir);
    }
}
