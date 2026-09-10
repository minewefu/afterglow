using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;

namespace Afterglow.Core.Diagnostics;

/// <summary>
/// A postmortem for one GPU's flight recording. <paramref name="GpuIndex"/> and
/// <paramref name="GpuName"/> name the card the evidence came from: on a
/// multi-GPU machine the offsets and load state in the report body belong to
/// exactly one card, and an unlabelled report was read as describing the whole
/// machine.
/// </summary>
public sealed record CrashReport(
    DateTimeOffset CrashedAt,
    string Headline,
    string ReportText,
    bool TuningApplied = false,
    uint? GpuIndex = null,
    string? GpuName = null);

/// <summary>
/// Startup crash forensics: if the previous flight recording ended without a
/// clean-shutdown marker, correlate its final seconds with the Windows System
/// event log (Kernel-Power 41, unexpected-shutdown 6008, WHEA, TDR 4101, and the
/// GPU kernel driver) and produce a plain-language postmortem. Run this BEFORE creating
/// the new session's <see cref="FlightRecorder"/> — the recorder rotates the
/// file this reads.
/// </summary>
public static class CrashForensics
{
    /// <summary>
    /// How stale an unclean session may be and still be correlated with the
    /// Windows event log. The startup banner already only surfaces reports from
    /// the last 72 h; this bounds the evidence itself.
    /// </summary>
    private static readonly TimeSpan MaxAnalysableAge = TimeSpan.FromDays(7);

    /// <summary>
    /// How long after the session end a boot-stamped hard-reset signature can
    /// still be attributed to it. Generous enough for a machine left off
    /// overnight — the case the +20 minute cap used to miss — but far short of
    /// the days over which an unrelated reset would otherwise be blamed on it.
    /// </summary>
    private static readonly TimeSpan MaxResetCorrelationWindow = TimeSpan.FromHours(24);

    /// <param name="gpuLabel">
    /// Which card's flight log this is (e.g. "GPU 1 — NVIDIA GeForce RTX 5090").
    /// It goes into the report body, so the pasted text says which GPU the
    /// offsets and load state below it belong to — on a multi-GPU machine an
    /// unlabelled postmortem reads as a statement about the whole system.
    /// </param>
    public static CrashReport? AnalyzePreviousSession(string flightDirectory, string? gpuLabel = null)
    {
        var session = FlightSession.Load(Path.Combine(flightDirectory, "current.log"));
        if (session is null || session.CleanShutdown || session.EndedAt is not { } end)
        {
            return null;
        }

        // The boot-stamped hard-reset signatures are searched from the session
        // end to the present, because the machine may have stayed off for a long
        // time. That has to be bounded somewhere: an unclean-but-uncrashed
        // session (Afterglow killed, machine fine) that is never followed by
        // another launch would otherwise keep matching the FIRST unexpected
        // shutdown that ever happens afterwards — days or weeks later — and
        // report it as a crash blaming whatever offsets were applied back then.
        // Past this age the evidence can no longer be tied to the session.
        if (DateTimeOffset.UtcNow - end > MaxAnalysableAge)
        {
            Log.Info(
                $"Skipping crash forensics: the unclean session ended {(DateTimeOffset.UtcNow - end).TotalDays:F0} " +
                "days ago, too long to correlate with the event log.");
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

            // How stale the reset evidence is. Without this the classifier
            // asserted an instant reset "under sustained load" from records that
            // could have been logged up to a day later.
            ResetLoggedAfter = events.ResetLoggedAt is { } at
                ? at - end.LocalDateTime
                : null,
        };

        var verdict = CrashClassifier.Classify(evidence);
        if (verdict is null)
        {
            return null;
        }

        string text = BuildReport(session, evidence, events, verdict, gpuLabel);
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
        return new CrashReport(
            end,
            verdict.Headline,
            text,
            TuningApplied: session.CoreOffsetMHz != 0 || session.MemOffsetMHz != 0);
    }

    internal sealed record EventEvidence(
        bool Available,
        int? BugcheckCode,
        bool UnexpectedShutdown,
        int WheaCount,
        int TdrCount,
        int DriverEventCount,

        /// <summary>When the accepted hard-reset record was logged (next boot), if any.</summary>
        DateTime? ResetLoggedAt = null);

    // One System-log walk per crash, not per GPU. A machine crash ends every
    // card's flight stream within the same second, and AppServices asks about
    // each card in turn — synchronously, on the UI thread, before the main
    // window exists. The sharing is explicit and scoped: the call site opens a
    // scope around its loop, and inside it a query is reused for any session
    // end within two minutes of the first. A process-wide cache keyed on a
    // truncated minute both missed crashes that straddled a tick and outlived
    // the startup it was meant for.
    private static readonly object EvidenceScopeLock = new();
    private static bool _evidenceScopeOpen;
    private static (DateTimeOffset Around, EventEvidence Evidence)? _scopedEvidence;

    /// <summary>
    /// Shares one System-log query among the analyses run inside the returned
    /// scope (every GPU of one startup). Outside a scope every call queries.
    /// </summary>
    public static IDisposable ShareSystemLogQueries()
    {
        lock (EvidenceScopeLock)
        {
            _evidenceScopeOpen = true;
            _scopedEvidence = null;
        }

        return new EvidenceScope();
    }

    private sealed class EvidenceScope : IDisposable
    {
        public void Dispose()
        {
            lock (EvidenceScopeLock)
            {
                _evidenceScopeOpen = false;
                _scopedEvidence = null;
            }
        }
    }

    private static EventEvidence QuerySystemLog(DateTimeOffset around)
    {
        lock (EvidenceScopeLock)
        {
            if (_evidenceScopeOpen && _scopedEvidence is { } shared
                && (around - shared.Around).Duration() <= TimeSpan.FromMinutes(2))
            {
                return shared.Evidence;
            }
        }

        var evidence = QuerySystemLogUncached(around);
        lock (EvidenceScopeLock)
        {
            if (_evidenceScopeOpen)
            {
                _scopedEvidence = (around, evidence);
            }
        }

        return evidence;
    }

    private static EventEvidence QuerySystemLogUncached(DateTimeOffset around)
    {
        int? bugcheck = null;
        bool unexpected = false;
        int whea = 0;
        int tdr = 0;
        int driver = 0;

        // How many system boots have started since the analysed session ended.
        // The crash's own hard-reset records belong to the first one; anything
        // from a later boot is a different event and must not be attributed here.
        int bootsAfterSession = 0;
        DateTime? resetEvidenceAt = null;

        // Kernel-mode display drivers whose logged faults count as GPU driver
        // events. Matching only NVIDIA's nvlddmkm meant an Intel Arc crash
        // always reported zero driver events, quietly weakening every verdict
        // on the vendor this release just added.
        static bool IsGpuKernelDriver(string provider) =>
            provider.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase)   // NVIDIA
            || provider.Contains("igdkmd", StringComparison.OrdinalIgnoreCase)  // Intel (igdkmd64/igdkmdn)
            || provider.Contains("amdkmdag", StringComparison.OrdinalIgnoreCase); // AMD

        try
        {
            // Two clocks, two windows.
            //
            // Kernel-Power 41 and EventLog 6008 are written during the NEXT boot,
            // so they carry BOOT time, not crash time. Capping the search at
            // +20 minutes meant any reset where the machine stayed off longer
            // produced all-false evidence and the user was told no crash had
            // occurred — so those two are searched up to the reset-correlation
            // window (24 h), not just +20 minutes.
            //
            // Everything else (TDR 4101, WHEA, GPU kernel-driver faults) is
            // stamped at the moment of the fault and belongs near the session
            // end. Counting those over the whole unbounded span would let an
            // unrelated TDR hours or days later manufacture a crash verdict out
            // of silence — TdrLogged is the classifier's first branch — or
            // downgrade a real bluescreen. They keep the tight window.
            var faultWindowEnd = around.AddMinutes(20);

            // The query stops at the reset-correlation window, not at "now".
            // Nothing later than that bound can change a counter — 41/6008 are
            // rejected past it and 4101/WHEA past the fault window — yet a
            // days-old unclean session enumerated up to seven days of System
            // records, per GPU, synchronously on the UI thread before the main
            // window appeared. The XPath is time-only, so the record count is
            // the whole cost.
            var now = DateTimeOffset.UtcNow;
            var searchEnd = around + MaxResetCorrelationWindow;
            string from = around.AddMinutes(-10).UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            string to = (searchEnd < now ? searchEnd : now).AddMinutes(1).UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            string query =
                $"*[System[TimeCreated[@SystemTime>='{from}' and @SystemTime<='{to}']]]";

            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query));
            for (EventRecord? record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    string provider = record.ProviderName ?? string.Empty;

                    // Records enumerate oldest-first, so a fault-stamped event is
                    // in the tight window only while its own timestamp is.
                    bool nearTheCrash = record.TimeCreated is not { } when
                        || when <= faultWindowEnd.LocalDateTime;

                    // The boot-stamped signatures are written by the boot that
                    // FOLLOWS the crash, so the ones belonging to this session are
                    // at or after its end. The query opens 10 minutes earlier to
                    // catch fault-stamped events, and without this gate a 41 from
                    // the PREVIOUS boot could be latched as this crash's bugcheck
                    // (likely whenever the analysed session began soon after a
                    // boot — the marginal-overclock signature).
                    bool afterTheSession = record.TimeCreated is not { } stamp
                        || stamp >= around.LocalDateTime;

                    // The boot-stamped reset signatures must also be CLOSE to the
                    // session. Counting boot markers alone was not enough: when
                    // Afterglow is killed while Windows keeps running (or the
                    // machine sleeps, which writes no 6005), the next real reset
                    // days later is still "the first boot after the session" and
                    // its records were accepted — inventing a hard-reset verdict
                    // that blamed whatever offsets were applied back then.
                    bool withinResetWindow = afterTheSession
                        && (record.TimeCreated is not { } resetStamp
                            || resetStamp <= around.Add(MaxResetCorrelationWindow).LocalDateTime);

                    switch (record.Id)
                    {
                        // "Event log service was started" — the boot marker. The
                        // hard-reset signatures for THIS crash are written by the
                        // first boot after it, so anything from a later boot
                        // belongs to a different event entirely.
                        case 6005 when afterTheSession:
                            bootsAfterSession++;
                            break;

                        // First 41 in that first boot wins: a later bluescreen
                        // must not displace the bugcheck code of the crash
                        // actually being analysed.
                        // The two signatures sit on OPPOSITE sides of their own
                        // boot marker, verified against a real System log:
                        // 6008 is written before 6005, Kernel-Power 41 after it
                        // (record ids 432, 434, 444 in one boot). So the first
                        // boot after the session is `== 0` for 6008 and `<= 1`
                        // for 41; using <= 1 for both let the SECOND boot's 6008
                        // through, which is exactly the unrelated-shutdown
                        // attribution this gate exists to prevent.
                        case 41 when withinResetWindow && bootsAfterSession <= 1
                            && provider.Contains("Kernel-Power", StringComparison.OrdinalIgnoreCase):
                            bugcheck ??= ReadBugcheck(record);
                            resetEvidenceAt ??= record.TimeCreated;
                            break;
                        case 6008 when withinResetWindow && bootsAfterSession == 0:
                            unexpected = true;
                            resetEvidenceAt ??= record.TimeCreated;
                            break;
                        case 4101 when nearTheCrash
                            && provider.Contains("Display", StringComparison.OrdinalIgnoreCase):
                            tdr++;
                            break;
                        default:
                            break;
                    }

                    if (!nearTheCrash)
                    {
                        continue;
                    }

                    if (provider.Contains("WHEA-Logger", StringComparison.OrdinalIgnoreCase))
                    {
                        whea++;
                    }
                    else if (IsGpuKernelDriver(provider))
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

        return new EventEvidence(true, bugcheck, unexpected, whea, tdr, driver, resetEvidenceAt);
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
        FlightSession session, CrashEvidence e, EventEvidence events, CrashVerdict verdict, string? gpuLabel)
    {
        var sb = new StringBuilder();
        if (gpuLabel is { Length: > 0 })
        {
            sb.Append("GPU: ").AppendLine(gpuLabel);
        }

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
                $"{last.PowerW?.ToString("F0", CultureInfo.InvariantCulture) ?? "?"} W, {last.UtilPct?.ToString(CultureInfo.InvariantCulture) ?? "?"}% load, " +
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

            // These two are stamped at the NEXT boot, so the gap between them and
            // the session end is the reader's only way to judge whether they
            // really belong to it. Printing the verdict without the timestamp hid
            // exactly that.
            if (events.ResetLoggedAt is { } resetAt)
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  (logged at the next boot, {resetAt:yyyy-MM-dd HH:mm:ss} — " +
                    $"{(resetAt - e.SessionEnd.LocalDateTime).TotalHours:F1} h after the session ended)"));
            }
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  WHEA hardware errors: {events.WheaCount}, driver resets (TDR 4101): {events.TdrCount}, " +
                $"GPU kernel-driver events: {events.DriverEventCount}"));
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
