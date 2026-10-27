namespace Afterglow.Core.Profiles;

/// <summary>
/// What startup tells the user about the profile it applies
/// (<c>AppSettings.ApplyProfileOnStart</c>) beyond "applied" (R2-B).
/// <para>
/// A profile marked stable still applies at startup when its stability passes
/// at its current settings came from the burn before R-01's fix: the stable
/// mark is the user's, and that was decided. But since those passes stopped
/// counting, the Profiles page says "Re-certify" while startup said nothing. It
/// now says so in the tray, once per launch (startup restores once), and under
/// the Settings picker, in the words every other surface uses
/// (<see cref="CertificationModes.StaleReason"/>).
/// </para>
/// </summary>
public static class StartupProfileNotice
{
    /// <summary>
    /// "Its sustained and transitions passes were earned with an older stress
    /// test, re-certify it on the Profiles page." Null when no mode stands at
    /// <see cref="CertificationStanding.OlderStressTest"/> — the same test as the
    /// Profiles page's "Re-certify" badge
    /// (<see cref="TuningProfile.CertifiedWithOlderStressTest"/>).
    /// </summary>
    public static string? OlderStressTestNote(TuningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string[] modes = [.. CertificationModes.All.Where(mode =>
            profile.StandingOf(mode) == CertificationStanding.OlderStressTest)];
        if (modes.Length == 0)
        {
            return null;
        }

        string list = modes.Length == 1
            ? modes[0]
            : $"{string.Join(", ", modes[..^1])} and {modes[^1]}";
        return $"Its {list} {(modes.Length == 1 ? "pass was" : "passes were")} " +
               $"{CertificationModes.StaleReason(CertificationStanding.OlderStressTest)} it on the Profiles page.";
    }

    /// <summary>The tray notice after the startup profile was applied, with the note above when it has one.</summary>
    public static string Applied(TuningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string applied = $"Startup profile '{profile.Name}' applied.";
        return OlderStressTestNote(profile) is { } note ? $"{applied} {note}" : applied;
    }
}
