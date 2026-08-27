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
}

public sealed record CrashVerdict(string Headline, string Interpretation, string Recommendation);

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
        // No crash signature at all: the app died (killed, updated) but Windows
        // kept running or shut down normally. Not a crash — stay silent.
        if (e is { BugcheckCode: null, UnexpectedShutdownLogged: false, TdrLogged: false, WheaErrorsLogged: false }
            && e.EventLogAvailable)
        {
            return null;
        }

        string offsets = DescribeOffsets(e);

        if (e.TdrLogged)
        {
            return new CrashVerdict(
                "The GPU driver reset (TDR) — Windows recovered without a reboot.",
                $"The display driver stopped responding and was reset. {offsets}A TDR under or after load " +
                "usually means the core clock or voltage margin ran out.",
                "Back off the core offset (or lock a lower clock) and re-validate with the burn test. " +
                "If it happened at stock, suspect the driver version or the card.");
        }

        if (e.BugcheckCode is int code and > 0)
        {
            return new CrashVerdict(
                $"Windows bluescreened (bugcheck 0x{code.ToString("X", CultureInfo.InvariantCulture)}).",
                $"The kernel crashed and wrote a bugcheck. {offsets}A bluescreen is not the typical " +
                "GPU-overclock signature (those usually die without one), so the tuning may be incidental — " +
                "but treat it as suspect until it recurs at stock.",
                "Check the minidump with WinDbg for the faulting driver. Run at stock offsets until the " +
                "cause is confirmed.");
        }

        if (e.WheaErrorsLogged)
        {
            return new CrashVerdict(
                "A hardware error was logged (WHEA) around the crash.",
                $"Windows recorded a machine-level hardware error. {offsets}WHEA sources are named in the " +
                "event details — PCIe/bus errors can be GPU-related; CPU cache or memory-controller errors " +
                "point at the platform (CPU/RAM/board) instead.",
                "Open Event Viewer → System → WHEA-Logger and note the error source. If it names PCI Express, " +
                "reduce the GPU offsets and reseat the power connector; otherwise look at platform stability.");
        }

        // From here on: the instant power-cut signature — reset/power-loss with
        // no bluescreen, no WHEA, no driver recovery. The timing relative to
        // load is what separates the failure modes.
        string storm = e.DisplayDriverEventCount >= 3
            ? $" The display driver logged {e.DisplayDriverEventCount.ToString(CultureInfo.InvariantCulture)} " +
              "errors in the final seconds — a GPU-side failure cascade preceded the reset."
            : string.Empty;

        if (e.HeavyLoadAtDeath)
        {
            string cause = e.CoreOffsetMHz > 0
                ? $"With +{e.CoreOffsetMHz.ToString(CultureInfo.InvariantCulture)} MHz core applied, the " +
                  "likeliest cause is core clock/voltage margin running out under full current draw; " +
                  "power delivery (PSU/connector) is the alternative."
                : "At stock clocks this points at power delivery (PSU, 12V-2x6 connector) or the card itself " +
                  "rather than tuning.";
            return new CrashVerdict(
                "Hard reset during sustained load — no bluescreen, no driver fault logged.",
                $"The machine lost power or reset instantly while the GPU was under sustained heavy load.{storm} {cause}",
                e.CoreOffsetMHz > 0
                    ? "Reduce the core offset and re-validate with the sustained burn; if it still resets at " +
                      "stock under load, test the power path."
                    : "Reseat the GPU power connector, try another PSU cable/outlet, and if it recurs, test " +
                      "with a different power supply.");
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
                "cycling stress pattern, which exercises exactly this regime.");
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
                "continue at stock, investigate the power path and platform.");
        }

        if (e.CoreOffsetMHz > 0)
        {
            return new CrashVerdict(
                "Hard reset at light load with a core offset applied.",
                $"The machine reset without a bluescreen or driver fault near idle, with " +
                $"+{e.CoreOffsetMHz.ToString(CultureInfo.InvariantCulture)} MHz core applied.{storm} Light, " +
                "bursty load lets the core boost to its very top clock bins at maximum voltage — a point " +
                "sustained burns never reach, because heavy load is power-limited to lower clocks.",
                "Validate with the Boost excursions stress pattern, and prefer a clock lock + offset over a " +
                "raw offset — the lock caps exactly this excursion.");
        }

        return new CrashVerdict(
            "Hard reset at stock settings — no bluescreen, no driver fault logged.",
            $"The machine lost power or reset instantly with no tuning applied.{storm} With clocks at stock, " +
            "this is not an overclocking failure: suspect power delivery (PSU, cables, 12V-2x6 connector), " +
            "the motherboard, or another component.",
            "Reseat power connectors, check PSU capacity and cabling, and consider platform-level tests " +
            "(memtest, CPU stress). Afterglow's tuning is not implicated by this event.");
    }

    private static string DescribeOffsets(CrashEvidence e) =>
        e.CoreOffsetMHz != 0 || e.MemOffsetMHz != 0
            ? $"Applied at the time: {e.CoreOffsetMHz.ToString("+0;-0", CultureInfo.InvariantCulture)} MHz core, " +
              $"{e.MemOffsetMHz.ToString("+0;-0", CultureInfo.InvariantCulture)} MHz memory. "
            : "No offsets were applied at the time. ";

    private static string FormatMinutes(double seconds)
    {
        int m = (int)(seconds / 60);
        int s = (int)(seconds % 60);
        return m > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{m} min {s} s")
            : string.Create(CultureInfo.InvariantCulture, $"{s} s");
    }
}
