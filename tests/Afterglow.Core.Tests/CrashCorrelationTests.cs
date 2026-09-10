using Afterglow.Core.Diagnostics;

namespace Afterglow.Core.Tests;

/// <summary>
/// Kernel-Power 41 and EventLog 6008 are written by the boot that FOLLOWS a
/// crash, so the search for them has to reach past the session end — but the
/// further out they sit, the weaker the tie to that session. These pin the rule
/// that a stale record must not produce a verdict asserting what the GPU was
/// doing at the moment of the reset, and must not name the user's overclock.
/// </summary>
public class CrashCorrelationTests
{
    private static CrashEvidence UnderLoadWithOffset() => new()
    {
        SessionEnd = DateTimeOffset.UtcNow,
        UnexpectedShutdownLogged = true,
        BugcheckCode = 0,
        HeavyLoadAtDeath = true,
        SecondsSinceHeavyLoadEnded = 0,
        CoreOffsetMHz = 250,
    };

    [Fact]
    public void A_reset_logged_at_the_next_boot_still_gets_the_timing_verdict()
    {
        // The machine was off for eight minutes: entirely normal for a reboot
        // after a hard reset, and the evidence genuinely belongs to this session.
        var verdict = CrashClassifier.Classify(UnderLoadWithOffset() with
        {
            ResetLoggedAfter = TimeSpan.FromMinutes(8),
        });

        Assert.NotNull(verdict);
        Assert.Contains("sustained load", verdict!.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("250", verdict.Interpretation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_reset_does_not_assert_an_instant_reset_or_blame_the_overclock()
    {
        // Afterglow was killed, Windows kept running, and something unrelated
        // reset the machine six hours later. The old widened search accepted
        // that record and reported "reset instantly while the GPU was under
        // sustained heavy load", naming the applied offset as the likely cause.
        var verdict = CrashClassifier.Classify(UnderLoadWithOffset() with
        {
            ResetLoggedAfter = TimeSpan.FromHours(6),
        });

        Assert.NotNull(verdict);
        Assert.DoesNotContain("instantly", verdict!.Interpretation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "likeliest cause is core clock", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);

        // It must say how stale the evidence is rather than hiding the gap.
        Assert.Contains("after this session ended", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be tied", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_stale_reset_at_stock_clocks_says_tuning_is_not_implicated()
    {
        var verdict = CrashClassifier.Classify(UnderLoadWithOffset() with
        {
            CoreOffsetMHz = 0,
            ResetLoggedAfter = TimeSpan.FromHours(6),
        });

        Assert.NotNull(verdict);
        Assert.Contains("not implicated", verdict!.Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_stale_bluescreen_is_hedged_too_not_just_a_stale_hard_reset()
    {
        // The bugcheck branch sits above the hard-reset branches, so a staleness
        // gate placed below it covered one path and left the other asserting
        // that last session's overclock is suspect for a bluescreen logged the
        // following evening.
        var verdict = CrashClassifier.Classify(UnderLoadWithOffset() with
        {
            BugcheckCode = 0x124,
            ResetLoggedAfter = TimeSpan.FromHours(20),
        });

        Assert.NotNull(verdict);
        Assert.Contains("after this session ended", verdict!.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be tied", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("treat it as suspect", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_whea_record_does_not_re_enable_the_unhedged_bluescreen_verdict()
    {
        // The staleness gate exempts WHEA because WHEA is fault-stamped — but an
        // exemption is only sound if the exempted branch is reached first. With
        // WHEA ordered below the bugcheck branch, one routine corrected-PCIe
        // event near the session end let a day-old bluescreen through unhedged,
        // naming the session's offsets.
        var verdict = CrashClassifier.Classify(UnderLoadWithOffset() with
        {
            WheaErrorsLogged = true,
            BugcheckCode = 0x124,
            ResetLoggedAfter = TimeSpan.FromHours(20),
        });

        Assert.NotNull(verdict);
        Assert.Contains("WHEA", verdict!.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bluescreened", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("treat it as suspect", verdict.Interpretation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_promptly_logged_bluescreen_keeps_its_normal_verdict()
    {
        var verdict = CrashClassifier.Classify(UnderLoadWithOffset() with
        {
            BugcheckCode = 0x124,
            ResetLoggedAfter = TimeSpan.FromMinutes(4),
        });

        Assert.NotNull(verdict);
        Assert.Contains("bluescreened", verdict!.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x124", verdict.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_fault_stamped_tdr_is_never_hedged_by_the_reset_gap()
    {
        // TDR 4101 is timestamped at the fault, not at the next boot, and is
        // only ever accepted inside the tight window — the reset gap says
        // nothing about it.
        var verdict = CrashClassifier.Classify(UnderLoadWithOffset() with
        {
            TdrLogged = true,
            ResetLoggedAfter = TimeSpan.FromHours(20),
        });

        Assert.NotNull(verdict);
        Assert.Contains("TDR", verdict!.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_with_no_recorded_gap_is_treated_as_promptly_correlated()
    {
        // Older records (and the fault-stamped signatures) carry no gap; they
        // must keep their existing behaviour rather than all becoming hedged.
        var evidence = UnderLoadWithOffset();

        Assert.Null(evidence.ResetLoggedAfter);
        Assert.True(evidence.ResetIsPromptlyCorrelated);
    }
}
