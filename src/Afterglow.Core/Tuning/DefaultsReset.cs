using Afterglow.Core.Fans;

namespace Afterglow.Core.Tuning;

/// <summary>
/// One card's return to driver defaults: the tuner's per-knob results, and what
/// the card's fan service answered when it was switched to Auto (null when the
/// card has no fan service).
/// </summary>
public sealed record CardResetResult(uint GpuIndex, ApplyResult Tuning, bool? FanServiceReleased)
{
    /// <summary>The knob name the tuners report the fan release under.</summary>
    public const string FansKnob = "fans";

    /// <summary>
    /// Every knob's outcome, with the fan release decided once. The NVIDIA
    /// tuner's own reset releases the fans as well, and it runs AFTER the fan
    /// service was switched to Auto (<see cref="DefaultsReset.Card"/>): once the
    /// curve is disarmed nothing in Afterglow re-pins the fans, so they are
    /// back under firmware control when EITHER release landed, and still under
    /// manual control only when both were refused.
    /// </summary>
    public IReadOnlyList<KnobResult> Results
    {
        get
        {
            if (FanServiceReleased is not bool serviceReleased)
            {
                return Tuning.Results;
            }

            var tunerFans = Tuning.Results.LastOrDefault(r => r.Knob == FansKnob);
            KnobResult? fans = tunerFans switch
            {
                { Applied: true } => tunerFans,
                not null => serviceReleased ? KnobResult.Ok(FansKnob, "auto") : tunerFans,

                // The tuner has nothing to say about fans (no fan control, or a
                // tuner that does not own them): a release the service had
                // accepted adds no line, a refused one must.
                null => serviceReleased
                    ? null
                    : KnobResult.Fail(FansKnob, "the driver did not accept the release to firmware control"),
            };

            var results = new List<KnobResult>(Tuning.Results.Count + 1);
            bool placed = false;
            foreach (var result in Tuning.Results)
            {
                if (result.Knob != FansKnob)
                {
                    results.Add(result);
                }
                else if (!placed && fans is not null)
                {
                    results.Add(fans);
                    placed = true;
                }
            }

            if (!placed && fans is not null)
            {
                results.Add(fans);
            }

            return results;
        }
    }

    /// <summary>True only when every knob was confirmed back at its default.</summary>
    public bool AllReturned => Results.All(r => r.Applied);

    /// <summary>True when at least one knob was confirmed back at its default.</summary>
    public bool AnyReturned => Results.Any(r => r.Applied);

    /// <summary>The knobs the driver refused, or did not confirm, at their defaults.</summary>
    public IReadOnlyList<string> NotReturned =>
        Results.Where(r => !r.Applied).Select(r => r.Knob).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Whether the fans are back under firmware control; null when the card
    /// reported nothing about fans at all (no fan control to release).
    /// </summary>
    public bool? FansReturned =>
        Results.LastOrDefault(r => r.Knob == FansKnob) is { } fans
            ? fans.Applied
            : FanServiceReleased is true ? true : null;

    /// <summary>The same outcome as one result, for callers that show a per-knob summary.</summary>
    public ApplyResult AsApplyResult()
    {
        var results = Results;
        return new ApplyResult(results.All(r => r.Applied), results);
    }
}

/// <summary>
/// Returning cards to driver defaults — the Tuning page's Reset, the panic
/// reset, the driver-crash (TDR) reset and the crash banner's reset — and the
/// words each one reports with.
/// <para>
/// The fan service is part of the reset (R-09): the tuner's reset handed the
/// fans back to firmware at the driver, but a fan service left in Curve mode
/// commanded them again on the very next telemetry tick.
/// </para>
/// <para>
/// The outcome is reported as it is (R-10): the panic and driver-crash resets
/// discarded every result and announced that all tuning was back at driver
/// defaults even when the driver had refused a write — most likely right after
/// a driver fault. Every message here names what may still be applied.
/// </para>
/// </summary>
public static class DefaultsReset
{
    private const string Unconfirmed = "the driver refused or did not confirm the reset";

    private const string StillStopping =
        "a stability run or V/F probe was still stopping and may re-apply settings. Check the Stability page.";

    /// <summary>
    /// Returns one card to driver defaults: its fan service to Auto FIRST, then
    /// the tuner. In that order the curve is disarmed before any release, so no
    /// telemetry tick can re-pin the fans between the tuner's release and the
    /// service's (R-09), and either release landing means the fans are free
    /// (see <see cref="CardResetResult.Results"/>).
    /// </summary>
    public static CardResetResult Card(uint gpuIndex, IGpuTuner tuner, FanControlService? fans)
    {
        ArgumentNullException.ThrowIfNull(tuner);
        bool? released = fans?.SetAuto();
        var tuning = tuner.ResetToDefaults();
        return new CardResetResult(gpuIndex, tuning, released);
    }

    /// <summary>True when every card's every knob was confirmed back at its default.</summary>
    public static bool AllReturned(IReadOnlyList<CardResetResult> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return cards.All(c => c.AllReturned);
    }

    /// <summary>
    /// What may still be applied, in words: "core offset, manual fan control"
    /// for one card; "GPU 0 manual fan control; GPU 1 core offset" when
    /// <paramref name="nameCards"/> (a machine with more than one GPU). Empty
    /// when everything returned.
    /// </summary>
    public static string NotReturnedList(IReadOnlyList<CardResetResult> cards, bool nameCards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var parts = cards
            .Where(c => !c.AllReturned)
            .Select(c =>
            {
                string knobs = string.Join(", ", c.NotReturned.Select(Describe));
                return nameCards ? $"GPU {c.GpuIndex} {knobs}" : knobs;
            });
        return string.Join("; ", parts);
    }

    /// <summary>The panic reset's tray message.</summary>
    public static string PanicMessage(IReadOnlyList<CardResetResult> cards, bool stopped, bool nameCards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (AllReturned(cards))
        {
            return stopped
                ? "Panic reset: all tuning returned to driver defaults."
                : "Panic reset: tuning returned to driver defaults, but " + StillStopping;
        }

        string message = $"Panic reset incomplete. May still be applied ({Unconfirmed}): " +
                         $"{NotReturnedList(cards, nameCards)}.";
        if (cards.Any(c => c.AnyReturned))
        {
            message += " Everything else returned to driver defaults.";
        }

        return stopped ? message : message + " Also, " + StillStopping;
    }

    /// <summary>The crash banner after a driver reset (TDR) triggered the reset.</summary>
    public static string DriverResetBanner(IReadOnlyList<CardResetResult> cards, bool nameCards, string description)
    {
        ArgumentNullException.ThrowIfNull(cards);
        string outcome = AllReturned(cards)
            ? "Afterglow returned all tuning to driver defaults."
            : "Afterglow tried to return all tuning to driver defaults, but these may still be applied " +
              $"({Unconfirmed}): {NotReturnedList(cards, nameCards)}.";
        return $"A GPU driver reset (TDR) was detected — {outcome} " +
               "If you were testing an overclock, it is likely unstable. " + description;
    }

    /// <summary>The tray balloon after a driver reset (TDR) triggered the reset.</summary>
    public static string DriverResetTray(IReadOnlyList<CardResetResult> cards, bool nameCards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return AllReturned(cards)
            ? "Tuning was reset to driver defaults for safety."
            : $"The reset to driver defaults was incomplete. May still be applied: {NotReturnedList(cards, nameCards)}.";
    }

    /// <summary>What an automation rule's "reset" action did, for its log line and tray balloon.</summary>
    public static string AutomationAction(IReadOnlyList<CardResetResult> cards, bool nameCards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return AllReturned(cards)
            ? "all tuning reset to driver defaults"
            : $"tuning reset incomplete; may still be applied ({Unconfirmed}): {NotReturnedList(cards, nameCards)}";
    }

    // A failed fan release leaves Afterglow's manual duty on the card; "fans"
    // alone in a list of things "still applied" reads as the fans themselves.
    private static string Describe(string knob) =>
        knob == CardResetResult.FansKnob ? "manual fan control" : knob;
}
