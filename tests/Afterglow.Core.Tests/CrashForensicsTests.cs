using Afterglow.Core.Diagnostics;

namespace Afterglow.Core.Tests;

/// <summary>
/// The classifier cases encode real crashes observed on the development
/// RTX 5090 (2026-08-26): four hard resets, all Kernel-Power 41 with
/// BugcheckCode 0, split between mid-burn deaths and post-load transition
/// deaths — plus the surrounding signatures (TDR, bluescreen, WHEA, stock).
/// </summary>
public class CrashClassifierTests
{
    private static CrashEvidence Base() => new()
    {
        SessionEnd = DateTimeOffset.UtcNow,
        UnexpectedShutdownLogged = true,
        BugcheckCode = 0,
    };

    [Fact]
    public void Mid_burn_death_with_core_offset_blames_load_margin()
    {
        var verdict = CrashClassifier.Classify(Base() with
        {
            HeavyLoadAtDeath = true,
            SecondsSinceHeavyLoadEnded = 0,
            CoreOffsetMHz = 210,
            MemOffsetMHz = 3150,
        });

        Assert.NotNull(verdict);
        Assert.Contains("during sustained load", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+210", verdict.Interpretation, StringComparison.Ordinal);
        Assert.Contains("margin", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Death_minutes_after_load_with_mem_offset_matches_transition_signature()
    {
        var verdict = CrashClassifier.Classify(Base() with
        {
            HeavyLoadAtDeath = false,
            SecondsSinceHeavyLoadEnded = 300,
            CoreOffsetMHz = 125,
            MemOffsetMHz = 2000,
        });

        Assert.NotNull(verdict);
        Assert.Contains("after load ended", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("transition", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Transition cycling", verdict.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void Driver_error_storm_before_death_is_reported_as_a_cascade()
    {
        var verdict = CrashClassifier.Classify(Base() with
        {
            SecondsSinceHeavyLoadEnded = 210,
            CoreOffsetMHz = 75,
            MemOffsetMHz = 2000,
            DisplayDriverEventCount = 30,
        });

        Assert.NotNull(verdict);
        Assert.Contains("30 errors", verdict.Interpretation, StringComparison.Ordinal);
        Assert.Contains("cascade", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Idle_death_with_only_core_offset_recommends_the_excursion_pattern()
    {
        var verdict = CrashClassifier.Classify(Base() with
        {
            CoreOffsetMHz = 175,
            MemOffsetMHz = 0,
        });

        Assert.NotNull(verdict);
        Assert.Contains("Boost excursions", verdict.Recommendation, StringComparison.Ordinal);
        Assert.Contains("lock", verdict.Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Idle_death_with_mem_offset_and_no_recent_load_still_points_at_transitions()
    {
        var verdict = CrashClassifier.Classify(Base() with
        {
            SecondsSinceHeavyLoadEnded = null,
            MemOffsetMHz = 3000,
        });

        Assert.NotNull(verdict);
        Assert.Contains("memory offset", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("transitions", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stock_hard_reset_exonerates_tuning()
    {
        var verdict = CrashClassifier.Classify(Base());

        Assert.NotNull(verdict);
        Assert.Contains("stock", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not implicated", verdict.Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tdr_wins_over_power_loss_classification()
    {
        var verdict = CrashClassifier.Classify(Base() with { TdrLogged = true, CoreOffsetMHz = 150 });

        Assert.NotNull(verdict);
        Assert.Contains("TDR", verdict.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void Nonzero_bugcheck_reports_a_bluescreen_with_the_code()
    {
        var verdict = CrashClassifier.Classify(Base() with { BugcheckCode = 0x133 });

        Assert.NotNull(verdict);
        Assert.Contains("bluescreened", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x133", verdict.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void Whea_errors_produce_a_hardware_error_verdict()
    {
        var verdict = CrashClassifier.Classify(Base() with { WheaErrorsLogged = true });

        Assert.NotNull(verdict);
        Assert.Contains("WHEA", verdict.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hard_reset_with_only_a_clock_lock_is_not_called_stock_or_blamed_on_it()
    {
        // R-43: a lock never reached the recording, so a lock-only session read
        // as "stock settings… Afterglow's tuning is not implicated". A lock caps
        // the clock rather than raising it, so it is not blamed either.
        var verdict = CrashClassifier.Classify(Base() with { LockedCoreClockMHz = 1800 });

        Assert.NotNull(verdict);
        Assert.Equal(CrashCause.HardReset, verdict.Cause);
        Assert.DoesNotContain("stock", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not implicated", verdict.Recommendation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1800 MHz clock lock", verdict.Interpretation, StringComparison.Ordinal);
        Assert.DoesNotContain("margin", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_memory_offset_below_the_transition_threshold_is_still_tuning()
    {
        // +300 MHz memory sits under the +500 transition-signature threshold and
        // used to fall through to "Hard reset at stock settings… with no tuning
        // applied" — a false statement about the session.
        var verdict = CrashClassifier.Classify(Base() with { MemOffsetMHz = 300 });

        Assert.NotNull(verdict);
        Assert.DoesNotContain("stock settings", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no tuning applied", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+300 MHz memory", verdict.Interpretation, StringComparison.Ordinal);
    }

    [Fact]
    public void Raised_v_f_points_under_load_are_read_as_core_margin()
    {
        var verdict = CrashClassifier.Classify(Base() with
        {
            HeavyLoadAtDeath = true,
            SecondsSinceHeavyLoadEnded = 0,
            VfPointsEdited = 4,
            VfPointMaxOffsetMHz = 200,
        });

        Assert.NotNull(verdict);
        Assert.Contains("margin", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+200", verdict.Interpretation, StringComparison.Ordinal);
        Assert.Contains("V/F points", verdict.Recommendation, StringComparison.Ordinal);
        Assert.DoesNotContain("At stock clocks", verdict.Interpretation, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_signature_verdict_names_its_cause_and_the_full_tuning()
    {
        var tuned = Base() with { CoreOffsetMHz = 100, LockedCoreClockMHz = 2400, VfPointsEdited = 1, VfPointMaxOffsetMHz = 150 };

        var tdr = CrashClassifier.Classify(tuned with { TdrLogged = true });
        var whea = CrashClassifier.Classify(tuned with { WheaErrorsLogged = true });
        var bsod = CrashClassifier.Classify(tuned with { BugcheckCode = 0x133 });
        var reset = CrashClassifier.Classify(tuned);
        var stale = CrashClassifier.Classify(tuned with { ResetLoggedAfter = TimeSpan.FromHours(6) });

        Assert.Equal(CrashCause.DriverReset, tdr!.Cause);
        Assert.Equal(CrashCause.HardwareError, whea!.Cause);
        Assert.Equal(CrashCause.Bluescreen, bsod!.Cause);
        Assert.Equal(CrashCause.HardReset, reset!.Cause);
        Assert.Equal(CrashCause.Uncorrelated, stale!.Cause);
        foreach (var verdict in new[] { tdr, whea, bsod })
        {
            Assert.Contains("+100 MHz core", verdict.Interpretation, StringComparison.Ordinal);
            Assert.Contains("2400 MHz clock lock", verdict.Interpretation, StringComparison.Ordinal);
            Assert.Contains("per-point V/F edits on 1 point (highest +150 MHz)", verdict.Interpretation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_crash_signals_means_no_report()
    {
        var verdict = CrashClassifier.Classify(new CrashEvidence
        {
            SessionEnd = DateTimeOffset.UtcNow,
        });

        Assert.Null(verdict);
    }

    // --- a session that ended inside a tuning write (R-43 follow-up) ------------

    private static readonly FlightWrite LockWrite =
        new(4, Tuning.TuningWriteKind.Lock, "setting a 210..2400 MHz clock lock");

    [Fact]
    public void Every_signature_inside_a_write_names_the_write_and_keeps_its_cause()
    {
        var inWrite = Base() with { CoreOffsetMHz = 100, WriteInProgress = LockWrite };

        var tdr = CrashClassifier.Classify(inWrite with { TdrLogged = true });
        var whea = CrashClassifier.Classify(inWrite with { WheaErrorsLogged = true });
        var bsod = CrashClassifier.Classify(inWrite with { BugcheckCode = 0x133 });
        var reset = CrashClassifier.Classify(inWrite);

        Assert.Equal(CrashCause.DriverReset, tdr!.Cause);
        Assert.Equal(CrashCause.HardwareError, whea!.Cause);
        Assert.Equal(CrashCause.Bluescreen, bsod!.Cause);
        Assert.Contains("0x133", bsod.Headline, StringComparison.Ordinal);
        Assert.Equal(CrashCause.HardReset, reset!.Cause);
        foreach (var verdict in new[] { tdr, whea, bsod, reset })
        {
            Assert.EndsWith("while Afterglow was setting a 210..2400 MHz clock lock.", verdict.Headline, StringComparison.Ordinal);
            Assert.Contains("In force before the write: +100 MHz core", verdict.Interpretation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_unreadable_event_log_still_says_the_recording_ends_inside_a_write_and_blames_nothing()
    {
        var verdict = CrashClassifier.Classify(new CrashEvidence
        {
            SessionEnd = DateTimeOffset.UtcNow,
            EventLogAvailable = false,
            WriteInProgress = LockWrite,
        });

        Assert.NotNull(verdict);
        Assert.Equal(CrashCause.Unknown, verdict.Cause);
        Assert.Contains("ends inside a tuning write", verdict.Interpretation, StringComparison.Ordinal);
        Assert.Contains("neither implicates nor clears", verdict.Interpretation, StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_reset_records_stay_hedged_even_inside_a_write()
    {
        var verdict = CrashClassifier.Classify(Base() with
        {
            ResetLoggedAfter = TimeSpan.FromHours(6),
            WriteInProgress = LockWrite,
        });

        Assert.NotNull(verdict);
        Assert.Equal(CrashCause.Uncorrelated, verdict.Cause);
        Assert.Contains("ends inside a tuning write", verdict.Interpretation, StringComparison.Ordinal);
        Assert.Contains("Re-run the stability tests", verdict.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_in_flight_with_windows_still_running_is_not_a_crash()
    {
        // Afterglow ended mid-write while Windows logged nothing: no crash to report.
        Assert.Null(CrashClassifier.Classify(new CrashEvidence
        {
            SessionEnd = DateTimeOffset.UtcNow,
            WriteInProgress = LockWrite,
        }));
    }
}

public class FlightSessionTests
{
    private static string Line(long ms, uint util, uint core = 2900, double power = 500) =>
        FormattableString.Invariant($"{ms}|{core}|15001|70|85|{power:F0}|{util}|1050|55");

    [Fact]
    public void Parses_markers_samples_and_clean_shutdown()
    {
        var session = FlightSession.Parse(
        [
            "#flight v1 started=1000",
            "#1000 offsets core=100 mem=500",
            Line(2000, 5),
            Line(3000, 7),
            "#4000 clean-shutdown",
        ]);

        Assert.True(session.CleanShutdown);
        Assert.Equal(100, session.CoreOffsetMHz);
        Assert.Equal(500, session.MemOffsetMHz);
        Assert.Equal(2, session.Samples.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(4000), session.EndedAt);
    }

    [Fact]
    public void Heavy_load_ending_before_death_yields_seconds_since()
    {
        var lines = new List<string> { "#flight v1 started=0" };
        for (int i = 0; i < 10; i++)
        {
            lines.Add(Line(i * 1000, 99));           // 10 s of sustained load
        }

        for (int i = 10; i < 250; i++)
        {
            lines.Add(Line(i * 1000, 3));            // 240 s of idle before death
        }

        var session = FlightSession.Parse(lines);

        Assert.False(session.CleanShutdown);
        Assert.False(session.HeavyLoadAtEnd());
        double? since = session.SecondsSinceHeavyLoadEnded();
        Assert.NotNull(since);
        Assert.InRange(since.Value, 235, 245);
    }

    [Fact]
    public void Heavy_load_running_at_death_reports_zero_seconds()
    {
        var lines = new List<string>();
        for (int i = 0; i < 30; i++)
        {
            lines.Add(Line(i * 1000, 97));
        }

        var session = FlightSession.Parse(lines);

        Assert.True(session.HeavyLoadAtEnd());
        Assert.Equal(0, session.SecondsSinceHeavyLoadEnded());
    }

    [Fact]
    public void Short_load_blips_do_not_count_as_sustained()
    {
        var lines = new List<string>();
        for (int i = 0; i < 60; i++)
        {
            lines.Add(Line(i * 1000, i % 20 == 0 ? 95u : 4u));   // isolated 1-s spikes
        }

        var session = FlightSession.Parse(lines);

        Assert.Null(session.SecondsSinceHeavyLoadEnded());
    }

    [Fact]
    public void Torn_final_line_is_ignored()
    {
        var session = FlightSession.Parse(
        [
            Line(1000, 50),
            "17561|29",                              // power died mid-write
        ]);

        Assert.Single(session.Samples);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1000), session.EndedAt);
    }
}
