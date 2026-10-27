using System.Globalization;

namespace Afterglow.Core.Diagnostics;

/// <summary>Everything the classifier knows about one unclean session end.</summary>
public sealed record CrashEvidence
{
    public required DateTimeOffset SessionEnd { get; init; }

    /// <summary>BugcheckCode from Kernel-Power event 41; null when no 41 was found.</summary>
    public int? BugcheckCode { get; init; }

    /// <summary>EventLog 6008 "previous shutdown was unexpected" present.</summary>
    public bool UnexpectedShutdownLogged { get; init; }

    public bool WheaErrorsLogged { get; init; }

    /// <summary>Display event 4101 — the driver reset and recovered (TDR).</summary>
    public bool TdrLogged { get; init; }

    /// <summary>nvlddmkm entries in the minutes before death (error-storm indicator).</summary>
    public int DisplayDriverEventCount { get; init; }

    public bool EventLogAvailable { get; init; } = true;

    public bool HeavyLoadAtDeath { get; init; }

    /// <summary>Null = no sustained load seen this session; 0 = still running at death.</summary>
    public double? SecondsSinceHeavyLoadEnded { get; init; }

    public int CoreOffsetMHz { get; init; }

    public int MemOffsetMHz { get; init; }

    /// <summary>The clock lock Afterglow had applied; null = none (or not recorded, see <see cref="TuningFullyRecorded"/>).</summary>
    public uint? LockedCoreClockMHz { get; init; }

    /// <summary>V/F points whose offset differed from the global core offset; 0 = none.</summary>
    public int VfPointsEdited { get; init; }

    /// <summary>The highest offset among the edited V/F points (meaningful when <see cref="VfPointsEdited"/> &gt; 0).</summary>
    public int VfPointMaxOffsetMHz { get; init; }

    /// <summary>
    /// False when the recording named only the offsets — written before the
    /// lock and V/F points were recorded, or with a V/F table that could not
    /// be read. Then "no offsets" does not establish "no tuning", and no
    /// verdict may say Afterglow's settings were not in play.
    /// </summary>
    public bool TuningFullyRecorded { get; init; } = true;

    /// <summary>True when any recorded knob was off stock.</summary>
    public bool TuningApplied =>
        CoreOffsetMHz != 0 || MemOffsetMHz != 0 || LockedCoreClockMHz is not null || VfPointsEdited > 0;

    /// <summary>True when a core offset or an edited V/F point raised the core clock.</summary>
    public bool CoreClockRaised => CoreOffsetMHz > 0 || (VfPointsEdited > 0 && VfPointMaxOffsetMHz > 0);

    /// <summary>
    /// How long after the session ended the hard-reset evidence was logged, when
    /// any was found. Those records are written by the NEXT boot, so a machine
    /// left off overnight legitimately shows hours here — but the further out it
    /// sits, the weaker the tie to this session. Null when no reset evidence was
    /// found at all.
    /// </summary>
    public TimeSpan? ResetLoggedAfter { get; init; }

    /// <summary>
    /// True when the reset evidence is close enough to the session end to
    /// support a verdict about WHAT the GPU was doing at the time. Beyond this
    /// the records may belong to something unrelated that happened later, so the
    /// verdict must not assert an instant reset "while under sustained load".
    /// </summary>
    public bool ResetIsPromptlyCorrelated =>
        ResetLoggedAfter is not { } gap || gap <= TimeSpan.FromMinutes(30);
}

/// <summary>
/// What kind of evidence a verdict rests on. <see cref="Unknown"/> is kept
/// apart from every confirmed signature on purpose (R-44): a session that ended
/// uncleanly while the event log could not be read is not evidence of a reset,
/// a power loss, or anything the tuning did.
/// </summary>
public enum CrashCause
{
    /// <summary>No crash evidence could be read; the session only ended uncleanly.</summary>
    Unknown,

    /// <summary>Reset or bluescreen records exist, but too long after the session to tie to it.</summary>
    Uncorrelated,

    /// <summary>The display driver timed out and was reset (TDR 4101).</summary>
    DriverReset,

    /// <summary>A machine-level hardware error was logged (WHEA).</summary>
    HardwareError,

    /// <summary>A bugcheck was recorded (Kernel-Power 41 with a non-zero code).</summary>
    Bluescreen,

    /// <summary>An instant reset or power loss: unexpected shutdown with no bluescreen, WHEA or TDR.</summary>
    HardReset,
}

public sealed record CrashVerdict(
    string Headline, string Interpretation, string Recommendation, CrashCause Cause = CrashCause.Unknown);

/// <summary>
/// Turns crash evidence into a plain-language verdict. Pure and deterministic
/// so the rules are unit-testable; the test suite encodes real field failures.
/// Verdicts state pattern matches, not certainties — the wording carries the
/// confidence.
/// </summary>
public static class CrashClassifier
{
    public static CrashVerdict? Classify(CrashEvidence e)
    {
        bool noSignature =
            e is { BugcheckCode: null, UnexpectedShutdownLogged: false, TdrLogged: false, WheaErrorsLogged: false };

        // No crash signature at all: the app died (killed, updated) but Windows
        // kept running or shut down normally. Not a crash — stay silent.
        if (noSignature && e.EventLogAvailable)
        {
            return null;
        }

        string tuning = DescribeTuning(e);

        // No signature because the log could not be READ (R-44). The session
        // still ended uncleanly, so silence could hide a real crash — but
        // falling through to the hard-reset branches below asserted an instant
        // power loss and named the offsets as its likeliest cause, from no
        // evidence at all. All that is known is that Afterglow did not shut
        // down; the verdict says exactly that and blames nothing.
        if (noSignature)
        {
            return new CrashVerdict(
                "Cause unknown — the Windows event log could not be read.",
                "Afterglow's recording stopped without a clean shutdown, but the Windows System event log could " +
                "not be read, so there is no evidence of what happened: a hard reset, a bluescreen, a driver reset " +
                $"and Afterglow simply being ended abnormally all look the same from here. {tuning}" +
                (e.TuningApplied
                    ? "Without crash evidence, this neither implicates nor clears that tuning."
                    : "Without crash evidence, nothing here points at a cause."),
                "Open Event Viewer → Windows Logs → System around the time the session ended and look for " +
                "Kernel-Power 41, EventLog 6008, Display 4101 or WHEA-Logger entries." +
                (e.TuningApplied
                    ? " If the machine did crash, treat these settings as unproven and re-run the stability " +
                      "tests before relying on them."
                    : string.Empty),
                CrashCause.Unknown);
        }

        // Staleness is judged BEFORE any verdict that reads the boot-stamped
        // evidence as a statement about this session. Both the bugcheck branch
        // and the hard-reset branches below draw conclusions — and name the
        // applied offsets — from records written at the next boot, which the
        // widened event-log search can now find up to a day later. Placing the
        // hedge lower down covered the hard-reset path and left the bluescreen
        // path asserting that last session's overclock is suspect for a
        // bluescreen from the following evening. The fault-stamped signatures
        // (TDR, WHEA) are timestamped at the fault and stay above this.
        if (!e.ResetIsPromptlyCorrelated && !e.TdrLogged && !e.WheaErrorsLogged)
        {
            string gap = e.ResetLoggedAfter is { } after ? FormatMinutes(after.TotalSeconds) : "some time";
            string what = e.BugcheckCode is int stale and > 0
                ? $"Windows bluescreened (bugcheck 0x{stale.ToString("X", CultureInfo.InvariantCulture)})"
                : "an unexpected shutdown was recorded";

            return new CrashVerdict(
                $"{char.ToUpperInvariant(what[0])}{what[1..]} {gap} after this session ended.",
                "Afterglow's recording stopped without a clean shutdown, and Windows logged this at the next " +
                $"boot — but {gap} later, so it cannot be tied to what the GPU was doing when this session " +
                "ended. It may be this crash, or something unrelated since.",
                e.TuningApplied
                    ? "Re-run the stability tests at these settings to establish whether they are actually " +
                      "implicated; do not treat this as proof either way."
                    : e.TuningFullyRecorded
                        ? "No tuning was applied in that session, so Afterglow's settings are not implicated."
                        : "The recording shows no clock offsets for that session and does not say whether a clock " +
                          "lock or per-point V/F edits were applied, so Afterglow's settings are unlikely to be " +
                          "implicated.",
                CrashCause.Uncorrelated);
        }

        if (e.TdrLogged)
        {
            return new CrashVerdict(
                "The GPU driver reset (TDR) — Windows recovered without a reboot.",
                $"The display driver stopped responding and was reset. {tuning}A TDR under or after load " +
                "usually means the core clock or voltage margin ran out.",
                "Back off the core offset (or lock a lower clock) and re-validate with the burn test. " +
                "If it happened at stock, suspect the driver version or the card.",
                CrashCause.DriverReset);
        }

        // WHEA sits ABOVE the bugcheck branch, alongside TDR. Both are
        // fault-stamped, which is why the staleness gate exempts them — but an
        // exemption is only sound if the exempted branch is actually reached
        // first. With WHEA below the bugcheck, any WHEA record near the session
        // end (the query filters by provider only, so a routine corrected-PCIe
        // event qualifies) let control fall through to the unhedged bugcheck
        // verdict, naming the session's offsets for a bluescreen logged up to a
        // day later — the exact attribution the hedge exists to prevent.
        if (e.WheaErrorsLogged)
        {
            return new CrashVerdict(
                "A hardware error was logged (WHEA) around the crash.",
                $"Windows recorded a machine-level hardware error. {tuning}WHEA sources are named in the " +
                "event details — PCIe/bus errors can be GPU-related; CPU cache or memory-controller errors " +
                "point at the platform (CPU/RAM/board) instead.",
                "Open Event Viewer → System → WHEA-Logger and note the error source. If it names PCI Express, " +
                "reduce the GPU offsets and reseat the power connector; otherwise look at platform stability.",
                CrashCause.HardwareError);
        }

        if (e.BugcheckCode is int code and > 0)
        {
            return new CrashVerdict(
                $"Windows bluescreened (bugcheck 0x{code.ToString("X", CultureInfo.InvariantCulture)}).",
                $"The kernel crashed and wrote a bugcheck. {tuning}A bluescreen is not the typical " +
                "GPU-overclock signature (those usually die without one), so the tuning may be incidental — " +
                "but treat it as suspect until it recurs at stock.",
                "Check the minidump with WinDbg for the faulting driver. Run at stock offsets until the " +
                "cause is confirmed.",
                CrashCause.Bluescreen);
        }

        // From here on: the instant power-cut signature — reset/power-loss with
        // no bluescreen, no WHEA, no driver recovery. The timing relative to
        // load is what separates the failure modes.
        string storm = e.DisplayDriverEventCount >= 3
            ? $" The display driver logged {e.DisplayDriverEventCount.ToString(CultureInfo.InvariantCulture)} " +
              "errors in the final seconds — a GPU-side failure cascade preceded the reset."
            : string.Empty;

        // A raised V/F point is a core overclock at that point's voltage, so it
        // takes the core-offset branches below. Before R-43 the recording never
        // carried per-point edits and such a crash fell through to "stock".
        bool coreOffset = e.CoreOffsetMHz > 0;

        // Staleness was already handled above, before the bugcheck branch, so
        // everything from here on is evidence promptly correlated to this
        // session and may be spoken about in those terms.
        if (e.HeavyLoadAtDeath)
        {
            string cause = e.CoreClockRaised
                ? $"With {DescribeCoreRaise(e)} applied, the likeliest cause is core clock/voltage margin running " +
                  "out under full current draw; power delivery (PSU/connector) is the alternative."
                : e.TuningApplied || !e.TuningFullyRecorded
                    ? "No core clock raise is on record, so this points at power delivery (PSU, 12V-2x6 connector) " +
                      $"or the card itself rather than core margin. {tuning}" +
                      (e.TuningApplied ? "That tuning is not ruled out." : string.Empty)
                    : "At stock clocks this points at power delivery (PSU, 12V-2x6 connector) or the card itself " +
                      "rather than tuning.";
            return new CrashVerdict(
                "Hard reset during sustained load — no bluescreen, no driver fault logged.",
                $"The machine lost power or reset instantly while the GPU was under sustained heavy load.{storm} {cause.TrimEnd()}",
                coreOffset
                    ? "Reduce the core offset and re-validate with the sustained burn; if it still resets at " +
                      "stock under load, test the power path."
                    : e.CoreClockRaised
                        ? "Lower the raised V/F points (or clear the per-point edits) and re-validate with the " +
                          "sustained burn; if it still resets at stock under load, test the power path."
                        : "Reseat the GPU power connector, try another PSU cable/outlet, and if it recurs, test " +
                          "with a different power supply.",
                CrashCause.HardReset);
        }

        if (e.SecondsSinceHeavyLoadEnded is double sinceLoad and > 0 and <= 600 && e.MemOffsetMHz >= 500)
        {
            return new CrashVerdict(
                "Hard reset shortly after load ended — the memory-offset transition signature.",
                $"The machine reset {FormatMinutes(sinceLoad)} after a sustained load finished, with " +
                $"+{e.MemOffsetMHz.ToString(CultureInfo.InvariantCulture)} MHz memory applied and no driver " +
                $"fault or bluescreen logged.{storm} This matches memory-offset instability at clock " +
                "transitions: the offset holds at sustained speed but glitches while the memory clock steps " +
                "down through its idle states — which is why it passes burn tests and dies at the desktop.",
                "Lower the memory offset substantially (or run it at 0), then validate with the Transition " +
                "cycling stress pattern, which exercises exactly this regime.",
                CrashCause.HardReset);
        }

        if (e.MemOffsetMHz >= 500)
        {
            return new CrashVerdict(
                "Hard reset at light load with a memory offset applied.",
                $"The machine reset without any bluescreen or driver fault while the GPU was near idle, with " +
                $"+{e.MemOffsetMHz.ToString(CultureInfo.InvariantCulture)} MHz memory applied.{storm} Desktop " +
                "use constantly steps the memory clock between idle and boost states; a marginal memory " +
                "offset can fail on those transitions even when every load test passes.",
                "Lower the memory offset and validate with the Transition cycling stress pattern. If resets " +
                "continue at stock, investigate the power path and platform.",
                CrashCause.HardReset);
        }

        if (coreOffset)
        {
            return new CrashVerdict(
                "Hard reset at light load with a core offset applied.",
                $"The machine reset without a bluescreen or driver fault near idle, with " +
                $"{DescribeCoreRaise(e)} applied.{storm} Light, " +
                "bursty load lets the core boost to its very top clock bins at maximum voltage — a point " +
                "sustained burns never reach, because heavy load is power-limited to lower clocks.",
                "Validate with the Boost excursions stress pattern, and prefer a clock lock + offset over a " +
                "raw offset — the lock caps exactly this excursion.",
                CrashCause.HardReset);
        }

        if (e.CoreClockRaised)
        {
            return new CrashVerdict(
                "Hard reset at light load with raised V/F points applied.",
                $"The machine reset without a bluescreen or driver fault near idle, with " +
                $"{DescribeCoreRaise(e)} applied.{storm} Light, bursty load sweeps the core across its whole " +
                "curve, up to the top clock bins at maximum voltage that sustained burns — power-limited to " +
                "lower clocks — never reach, so a raised point can fail at the desktop after passing every burn.",
                "Validate with the Boost excursions stress pattern; if resets continue, clear the per-point " +
                "V/F edits and see whether they stop.",
                CrashCause.HardReset);
        }

        if (!e.TuningApplied && e.TuningFullyRecorded)
        {
            return new CrashVerdict(
                "Hard reset at stock settings — no bluescreen, no driver fault logged.",
                $"The machine lost power or reset instantly with no tuning applied.{storm} With clocks at stock, " +
                "this is not an overclocking failure: suspect power delivery (PSU, cables, 12V-2x6 connector), " +
                "the motherboard, or another component.",
                "Reseat power connectors, check PSU capacity and cabling, and consider platform-level tests " +
                "(memtest, CPU stress). Afterglow's tuning is not implicated by this event.",
                CrashCause.HardReset);
        }

        // Offsets at stock, but the recording cannot vouch for the rest — one
        // written before locks and V/F points were recorded, or whose V/F table
        // could not be read. "No tuning applied" would be a claim it never made.
        if (!e.TuningApplied)
        {
            return new CrashVerdict(
                "Hard reset with no clock offsets on record — no bluescreen, no driver fault logged.",
                $"The machine lost power or reset instantly with no clock offsets on record.{storm} The recording " +
                "does not say whether a clock lock or per-point V/F edits were in place, so tuning cannot be fully " +
                "ruled out — but with the offsets at stock this does not look like an overclocking failure: " +
                "suspect power delivery (PSU, cables, 12V-2x6 connector), the motherboard, or another component.",
                "Reseat power connectors, check PSU capacity and cabling, and consider platform-level tests " +
                "(memtest, CPU stress).",
                CrashCause.HardReset);
        }

        // Tuning was applied, but none that matches a known failure pattern: a
        // clock lock (a cap, not a raise), a negative offset, a small memory
        // offset, per-point edits that only lower points. The old fallback
        // called all of these "stock settings… not implicated" because it only
        // ever looked at the two offsets being positive.
        return new CrashVerdict(
            "Hard reset with tuning applied — no bluescreen, no driver fault logged.",
            $"The machine lost power or reset instantly.{storm} {tuning}None of it raises the core clock, and no " +
            "memory offset of +500 MHz or more was applied, so this does not match a known overclocking-failure " +
            "pattern: power delivery (PSU, cables, 12V-2x6 connector), the motherboard or another component are " +
            "likelier. The tuning was in place, though, and is not ruled out.",
            "Reset to driver defaults and see whether the resets stop; if they continue at stock, check the power " +
            "connectors, PSU and cabling, and the platform.",
            CrashCause.HardReset);
    }

    /// <summary>
    /// The "Applied at the time" sentence: offsets, clock lock and V/F-point
    /// edits as recorded, and — when the recording could not say — that it
    /// could not, rather than "nothing".
    /// </summary>
    private static string DescribeTuning(CrashEvidence e)
    {
        var parts = new List<string>(3);
        if (e.CoreOffsetMHz != 0 || e.MemOffsetMHz != 0)
        {
            parts.Add($"{e.CoreOffsetMHz.ToString("+0;-0", CultureInfo.InvariantCulture)} MHz core, " +
                      $"{e.MemOffsetMHz.ToString("+0;-0", CultureInfo.InvariantCulture)} MHz memory");
        }

        if (e.LockedCoreClockMHz is uint lockMHz)
        {
            parts.Add($"a {lockMHz.ToString(CultureInfo.InvariantCulture)} MHz clock lock");
        }

        if (e.VfPointsEdited > 0)
        {
            parts.Add(DescribeVfEdits(e));
        }

        if (parts.Count > 0)
        {
            return $"Applied at the time: {string.Join(", ", parts)}. ";
        }

        return e.TuningFullyRecorded
            ? "No tuning was applied at the time. "
            : "The recording shows no clock offsets and does not say whether a clock lock or per-point V/F " +
              "edits were applied. ";
    }

    private static string DescribeVfEdits(CrashEvidence e) =>
        $"per-point V/F edits on {e.VfPointsEdited.ToString(CultureInfo.InvariantCulture)} " +
        $"point{(e.VfPointsEdited == 1 ? string.Empty : "s")} " +
        $"(highest {e.VfPointMaxOffsetMHz.ToString("+0;-0;0", CultureInfo.InvariantCulture)} MHz)";

    /// <summary>What raised the core clock, for the core-margin verdicts.</summary>
    private static string DescribeCoreRaise(CrashEvidence e)
    {
        string? offset = e.CoreOffsetMHz > 0
            ? $"+{e.CoreOffsetMHz.ToString(CultureInfo.InvariantCulture)} MHz core"
            : null;
        string? points = e.VfPointsEdited > 0 && e.VfPointMaxOffsetMHz > 0
            ? $"V/F points raised by up to +{e.VfPointMaxOffsetMHz.ToString(CultureInfo.InvariantCulture)} MHz"
            : null;

        return offset is not null && points is not null
            ? $"{offset} and {points}"
            : offset ?? points ?? "no core raise";
    }

    private static string FormatMinutes(double seconds)
    {
        // The staleness branch reports gaps of up to 24 h through this helper,
        // which was written for the ≤600 s post-load case — so an overnight
        // reset rendered as "1200 min 0 s" in the startup banner while the
        // report body printed the same quantity in hours.
        if (seconds >= 3600)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600.0:F1} h");
        }

        int m = (int)(seconds / 60);
        int s = (int)(seconds % 60);
        return m > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{m} min {s} s")
            : string.Create(CultureInfo.InvariantCulture, $"{s} s");
    }
}
