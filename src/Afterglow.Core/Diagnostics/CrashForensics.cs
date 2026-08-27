using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;

namespace Afterglow.Core.Diagnostics;

public sealed record CrashReport(DateTimeOffset CrashedAt, string Headline, string ReportText);

/// <summary>
/// Startup crash forensics: if the previous flight recording ended without a
/// clean-shutdown marker, correlate its final seconds with the Windows System
/// event log (Kernel-Power 41, unexpected-shutdown 6008, WHEA, TDR 4101,
/// nvlddmkm) and produce a plain-language postmortem. Run this BEFORE creating
/// the new session's <see cref="FlightRecorder"/> — the recorder rotates the
/// file this reads.
/// </summary>
public static class CrashForensics
{
    public static CrashReport? AnalyzePreviousSession(string flightDirectory)
    {
        var session = FlightSession.Load(Path.Combine(flightDirectory, "current.log"));
        if (session is null || session.CleanShutdown || session.EndedAt is not { } end)
        {
            return null;
        }

        var events = QuerySystemLog(end);
        var evidence = new CrashEvidence
        {
            SessionEnd = end,
            BugcheckCode = events.BugcheckCode,
            UnexpectedShutdownLogged = events.UnexpectedShutdown,
            WheaErrorsLogged = events.WheaCount > 0,
            TdrLogged = events.TdrCount > 0,
            DisplayDriverEventCount = events.DriverEventCount,
            EventLogAvailable = events.Available,
            HeavyLoadAtDeath = session.HeavyLoadAtEnd(),
            SecondsSinceHeavyLoadEnded = session.SecondsSinceHeavyLoadEnded(),
            CoreOffsetMHz = session.CoreOffsetMHz,
            MemOffsetMHz = session.MemOffsetMHz,
        };

        var verdict = CrashClassifier.Classify(evidence);
        if (verdict is null)
        {
            return null;
        }

        string text = BuildReport(session, evidence, events, verdict);
        try
        {
            File.WriteAllText(Path.Combine(flightDirectory, "last-crash-report.txt"), text);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        Log.Info($"Crash forensics: {verdict.Headline}");
        return new CrashReport(end, verdict.Headline, text);
    }

    internal sealed record EventEvidence(
        bool Available,
        int? BugcheckCode,
        bool UnexpectedShutdown,
        int WheaCount,
        int TdrCount,
        int DriverEventCount);

    private static EventEvidence QuerySystemLog(DateTimeOffset around)
    {
        int? bugcheck = null;
        bool unexpected = false;
        int whea = 0;
        int tdr = 0;
        int driver = 0;

        try
        {
            string from = around.AddMinutes(-10).UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            string to = around.AddMinutes(20).UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            string query =
                $"*[System[TimeCreated[@SystemTime>='{from}' and @SystemTime<='{to}']]]";

            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query));
            for (EventRecord? record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    string provider = record.ProviderName ?? string.Empty;
                    switch (record.Id)
                    {
                        case 41 when provider.Contains("Kernel-Power", StringComparison.OrdinalIgnoreCase):
                            bugcheck = ReadBugcheck(record) ?? bugcheck;
                            break;
                        case 6008:
                            unexpected = true;
                            break;
                        case 4101 when provider.Contains("Display", StringComparison.OrdinalIgnoreCase):
                            tdr++;
                            break;
                        default:
                            break;
                    }

                    if (provider.Contains("WHEA-Logger", StringComparison.OrdinalIgnoreCase))
                    {
                        whea++;
                    }
                    else if (provider.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase))
                    {
                        driver++;
                    }
                }
            }
        }
        catch (EventLogException)
        {
            return new EventEvidence(false, null, false, 0, 0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return new EventEvidence(false, null, false, 0, 0, 0);
        }

        return new EventEvidence(true, bugcheck, unexpected, whea, tdr, driver);
    }

    private static int? ReadBugcheck(EventRecord record)
    {
        try
        {
            // Kernel-Power 41 lays BugcheckCode out as the first EventData property.
            if (record.Properties is { Count: > 0 } props &&
                props[0].Value is not null)
            {
                return Convert.ToInt32(props[0].Value, CultureInfo.InvariantCulture);
            }
        }
        catch (EventLogException)
        {
        }
        catch (FormatException)
        {
        }
        catch (InvalidCastException)
        {
        }
        catch (OverflowException)
        {
        }

        return null;
    }

    private static string BuildReport(
        FlightSession session, CrashEvidence e, EventEvidence events, CrashVerdict verdict)
    {
        var sb = new StringBuilder();
        sb.Append("Session ended: ").Append(
            e.SessionEnd.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.AppendLine(" (no clean shutdown recorded)");
        sb.Append("Verdict: ").AppendLine(verdict.Headline);
        sb.AppendLine();

        sb.AppendLine("Recorded by the flight recorder:");
        if (session.LastSample() is { } last)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  Final telemetry: {last.CoreMHz?.ToString(CultureInfo.InvariantCulture) ?? "?"} MHz core, " +
                $"{last.PowerW ?? 0:F0} W, {last.UtilPct?.ToString(CultureInfo.InvariantCulture) ?? "?"}% load, " +
                $"{last.TempC?.ToString(CultureInfo.InvariantCulture) ?? "?"} °C"));
        }

        sb.AppendLine(e.HeavyLoadAtDeath
            ? "  A sustained heavy load was running at the moment of death."
            : e.SecondsSinceHeavyLoadEnded is double s
                ? string.Create(CultureInfo.InvariantCulture,
                    $"  The last sustained heavy load ended {s / 60:F1} min before death.")
                : "  No sustained heavy load was seen this session.");
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  Offsets applied: {e.CoreOffsetMHz:+0;-0;+0} MHz core, {e.MemOffsetMHz:+0;-0;+0} MHz memory"));
        sb.AppendLine();

        sb.AppendLine("Windows event log around the crash:");
        if (!events.Available)
        {
            sb.AppendLine("  (event log could not be read)");
        }
        else
        {
            sb.AppendLine(events.BugcheckCode is int bc
                ? string.Create(CultureInfo.InvariantCulture,
                    $"  Kernel-Power 41: found, BugcheckCode {bc} {(bc == 0 ? "(no bluescreen — instant power loss/reset)" : "(bluescreen)")}")
                : "  Kernel-Power 41: not found");
            sb.AppendLine(events.UnexpectedShutdown
                ? "  Unexpected-shutdown 6008: found"
                : "  Unexpected-shutdown 6008: not found");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  WHEA hardware errors: {events.WheaCount}, driver resets (TDR 4101): {events.TdrCount}, " +
                $"nvlddmkm driver events: {events.DriverEventCount}"));
        }

        sb.AppendLine();
        sb.AppendLine("Interpretation:");
        sb.Append("  ").AppendLine(verdict.Interpretation);
        sb.AppendLine();
        sb.AppendLine("Recommendation:");
        sb.Append("  ").AppendLine(verdict.Recommendation);
        return sb.ToString();
    }
}
