using System.Globalization;
using Afterglow.Core.Fans;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Profiles;
using Afterglow.Core.Settings;

namespace Afterglow.Core.Tuning;

/// <summary>One card's fan configuration, as a game rule found it.</summary>
public sealed record FanRestoreState(FanMode Mode, uint FixedPct, FanCurveConfig? Curve);

/// <summary>
/// What a game rule's profile can change on the card it lands on, read from
/// THAT card just before the profile is applied, together with the profile
/// itself — so the exit restore puts back exactly what the game could have
/// changed, on exactly that card (F-16, F-21).
/// </summary>
public sealed record PreGameSnapshot
{
    /// <summary>Executable of the rule that applied the game profile.</summary>
    public required string Executable { get; init; }

    /// <summary>The card the game profile landed on (device index).</summary>
    public required uint GpuIndex { get; init; }

    /// <summary>That card's NVML UUID; the restore profile is stamped with it.</summary>
    public string? GpuUuid { get; init; }

    /// <summary>The profile the game applied — decides which knobs need putting back.</summary>
    public required TuningProfile GameProfile { get; init; }

    public required int CoreOffsetMHz { get; init; }

    public required int MemOffsetMHz { get; init; }

    /// <summary>Enforced power limit in watts; 0 when it could not be read.</summary>
    public required double PowerLimitW { get; init; }

    public uint? VoltageBoostPct { get; init; }

    public uint? LockedCoreClockMHz { get; init; }

    /// <summary>The device's profile floors (<see cref="IGpuTuner.ProfileFloors"/>), so the restore validates as its tuner will.</summary>
    public uint LockFloorMHz { get; init; } = 210;

    public double PowerFloorW { get; init; } = 50;

    /// <summary>True when the card has a temperature-limit control at all.</summary>
    public bool TempLimitSupported { get; init; }

    /// <summary>Temperature limit in force (°C); null when unsupported or unreadable.</summary>
    public int? TempLimitC { get; init; }

    /// <summary>True when the card has per-point V/F control at all.</summary>
    public bool VfPointsSupported { get; init; }

    /// <summary>
    /// Every live V/F slot's offset, zeros included — the whole table, so a
    /// restore writes back the exact curve rather than only its non-zero
    /// points. Null when unsupported or the table could not be read.
    /// </summary>
    public IReadOnlyDictionary<int, int>? VfPointOffsetsMHz { get; init; }

    /// <summary>True when that table carried per-point shape (see VfPointPlanner.HasPerPointShape).</summary>
    public bool VfHadShape { get; init; }

    /// <summary>The card's own fan configuration; null when it could not be determined.</summary>
    public FanRestoreState? Fans { get; init; }
}

/// <summary>The exit restore: the profile to apply, the fans to set, and what cannot be put back.</summary>
public sealed record GameRestorePlan(TuningProfile Profile, FanRestoreState? Fans, IReadOnlyList<string> NotRestored);

/// <summary>
/// How the app hands a game session from the game whose profile was active,
/// which exited, to a configured game still running (GameWatcher.GameHandedOff).
/// </summary>
public enum GameHandoff
{
    /// <summary>
    /// No pre-game snapshot: nothing of the session is on any card (the first
    /// game's profile was missing or refused). The remaining game starts as if
    /// it had started alone, capture included — there is no earlier state to keep.
    /// </summary>
    StartFresh,

    /// <summary>
    /// The remaining game's profile lands on the snapshot's card: put the
    /// pre-game state back without consuming the snapshot, apply the new
    /// profile on top, and keep the snapshot (<see cref="GameSessionRestore.HandedOff"/>)
    /// as the restore target. Applied straight over the exited game's profile,
    /// it would leave that profile's temperature limit, curve or fans on the
    /// card wherever the new one has no value of its own.
    /// </summary>
    Swap,

    /// <summary>
    /// The remaining game's profile cannot take over on that card: there is
    /// none, it is invalid, stamped for another GPU, or meant for another card.
    /// The exited game's part ends as its own exit would (its RevertOnExit
    /// decides), then the remaining game starts as if it had started alone —
    /// on another card that includes capturing that card's state, which is the
    /// only restore target that card has.
    /// </summary>
    EndThenStart,
}

/// <summary>
/// Pre-game capture and post-game restore for game rules. The restore used to
/// rebuild only what ReadCurrent can see (core/mem/power/boost/lock), so a game
/// profile's temperature limit and per-point V/F curve stayed applied after the
/// game while the tray said everything was restored (F-21); and it took the
/// fan configuration from the Fans page editor, which mirrors the SELECTED
/// card, and pushed it onto the card the game profile landed on (F-16).
/// </summary>
public static class GameSessionRestore
{
    /// <summary>Reads everything the restore may need from the card the game profile is about to land on.</summary>
    public static PreGameSnapshot Capture(
        IGpuTuner tuner, uint gpuIndex, string executable, TuningProfile gameProfile, FanRestoreState? fans)
    {
        ArgumentNullException.ThrowIfNull(tuner);
        var current = tuner.ReadCurrent();

        IReadOnlyDictionary<int, int>? table = null;
        bool shape = false;
        if (tuner.Capabilities.SupportsVfPoints &&
            tuner.TryReadVfPoints(out var points) == NvapiStatus.Ok &&
            points.Count > 0)
        {
            table = points.ToDictionary(p => p.Index, p => p.OffsetMHz);
            shape = VfPointPlanner.HasPerPointShape(points);
        }

        return new PreGameSnapshot
        {
            Executable = executable,
            GpuIndex = gpuIndex,
            GpuUuid = tuner.GpuUuid,
            GameProfile = gameProfile,
            CoreOffsetMHz = current.CoreOffsetMHz,
            MemOffsetMHz = current.MemOffsetMHz,
            PowerLimitW = current.PowerLimitW ?? 0,
            VoltageBoostPct = current.VoltageBoostPct,
            // The lock Afterglow applied, never ReadCurrent's observation: on a
            // driver with a lock getter that can be a factory ceiling, and writing
            // it back at game exit would make it a clamp no release can adopt.
            LockedCoreClockMHz = tuner.AppliedLockMHz,
            LockFloorMHz = tuner.ProfileFloors.LockFloorMHz,
            PowerFloorW = tuner.ProfileFloors.PowerFloorW,
            TempLimitSupported = tuner.Capabilities.SupportsTempLimit,
            TempLimitC = tuner.ReadTempLimitC(),
            VfPointsSupported = tuner.Capabilities.SupportsVfPoints,
            VfPointOffsetsMHz = table,
            VfHadShape = shape,
            Fans = fans,
        };
    }

    /// <summary>
    /// The fan configuration one card is running, from its own fan service
    /// (the mode, and the duty it actually commanded) and its own persisted
    /// settings (the curve the service does not expose) — never from the Fans
    /// page editor, which shows only the selected card. Null when the state
    /// cannot be reconstructed: then the fans are left as the game set them
    /// rather than handed a guessed configuration.
    /// </summary>
    public static FanRestoreState? CaptureFans(FanControlMode liveMode, double? commandedDuty, FanSettings saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return liveMode switch
        {
            FanControlMode.Auto => new FanRestoreState(FanMode.Auto, saved.FixedDutyPct, null),
            FanControlMode.Fixed when commandedDuty is double duty =>
                new FanRestoreState(FanMode.Fixed, (uint)Math.Clamp(Math.Round(duty), 0, 100), null),
            FanControlMode.Fixed when saved.Mode == "fixed" =>
                new FanRestoreState(FanMode.Fixed, saved.FixedDutyPct, null),
            FanControlMode.Curve when saved.Mode == "curve" && saved.Curve.Validate() is null =>
                new FanRestoreState(FanMode.Curve, saved.FixedDutyPct, saved.Curve),
            _ => null,
        };
    }

    /// <summary>
    /// True when applying this profile in game context changes the fans. An
    /// Auto-fan game profile has no fan opinion (MainViewModel.ApplyProfileFull),
    /// so its exit must not touch the fans either.
    /// </summary>
    public static bool ChangesFans(TuningProfile gameProfile)
    {
        ArgumentNullException.ThrowIfNull(gameProfile);
        return gameProfile.FanMode switch
        {
            FanMode.Fixed => true,
            FanMode.Curve => gameProfile.FanCurve is not null,
            _ => false,
        };
    }

    /// <summary>
    /// The restore for one game session. Core/mem offsets, voltage boost and
    /// the lock come back as before; the power limit when it was readable; the
    /// temperature limit and the whole V/F table only when the game could have
    /// changed them; the fans only when the game profile set them. Anything the
    /// game changed that cannot be put back is listed, never claimed restored.
    /// Each optional knob is checked against the profile validator on its own,
    /// so one value the validator rejects cannot get the whole restore refused.
    /// </summary>
    public static GameRestorePlan Plan(PreGameSnapshot pre)
    {
        ArgumentNullException.ThrowIfNull(pre);
        var game = pre.GameProfile;
        var notRestored = new List<string>();

        // Stamped with the card it was read from: should this ever be handed
        // to another card, the tuner's identity gate refuses it outright.
        var profile = new TuningProfile
        {
            Name = "pre-game state",
            GpuUuid = pre.GpuUuid,
            CoreOffsetMHz = pre.CoreOffsetMHz,
            MemOffsetMHz = pre.MemOffsetMHz,
            VoltageBoostPct = pre.VoltageBoostPct,
            LockedCoreClockMHz = pre.LockedCoreClockMHz,
        };

        if (pre.PowerLimitW > 0)
        {
            profile = WithIfValid(pre, profile, profile with { PowerLimitW = pre.PowerLimitW }, notRestored,
                $"power limit ({Fmt(pre.PowerLimitW)} W before the game is outside what a profile can carry)");
        }
        else if (game.PowerLimitW is not null)
        {
            notRestored.Add("power limit (its value before the game could not be read)");
        }

        if (game.TempLimitC is uint gameTemp && pre.TempLimitSupported)
        {
            if (pre.TempLimitC is int before && before > 0)
            {
                profile = WithIfValid(pre, profile, profile with { TempLimitC = (uint)before }, notRestored,
                    $"temperature limit ({before} °C before the game is outside what a profile can carry)");
            }
            else
            {
                notRestored.Add($"temperature limit (its value before the game could not be read; the game set {gameTemp} °C)");
            }
        }

        // The global core offset shares the per-point table on the hardware
        // this was measured on, so a game profile can change the curve without
        // carrying a single point: its core-offset write levels a shaped curve.
        bool curveTouched = pre.VfPointsSupported &&
            (game.VfPointOffsetsMHz is { Count: > 0 } || game.CapturedVfPoints || pre.VfHadShape);
        if (curveTouched)
        {
            if (pre.VfPointOffsetsMHz is { Count: > 0 } table)
            {
                profile = WithIfValid(pre, profile, profile with { VfPointOffsetsMHz = table, CapturedVfPoints = true }, notRestored,
                    "per-point V/F curve (its state before the game is outside what a profile can carry)");
            }
            else
            {
                notRestored.Add("per-point V/F curve (its state before the game could not be read)");
            }
        }

        FanRestoreState? fans = null;
        if (ChangesFans(game))
        {
            if (pre.Fans is { } before)
            {
                fans = before;
            }
            else
            {
                notRestored.Add("fans (their configuration before the game could not be determined; left as the game set them)");
            }
        }

        return new GameRestorePlan(profile, fans, notRestored);
    }

    /// <summary>
    /// Decides a hand-off (R1). <paramref name="next"/> is the remaining game's
    /// profile (null when it could not be loaded), <paramref name="nextGpuIndex"/>
    /// and <paramref name="nextGpuUuid"/> the card it would land on. A swap needs
    /// the snapshot's own card — the same identity test as the exit restore — and
    /// a profile that card's tuner would accept, so the pre-game state is never
    /// put back for a profile that then gets refused.
    /// </summary>
    public static GameHandoff PlanHandoff(
        PreGameSnapshot? pre, TuningProfile? next, uint? nextGpuIndex, string? nextGpuUuid)
    {
        if (pre is null)
        {
            return GameHandoff.StartFresh;
        }

        bool sameCard = nextGpuIndex == pre.GpuIndex &&
            (pre.GpuUuid is null || string.Equals(nextGpuUuid, pre.GpuUuid, StringComparison.OrdinalIgnoreCase));
        return next is not null && sameCard && next.AppliesToGpu(nextGpuUuid) &&
               next.Validate(pre.LockFloorMHz, pre.PowerFloorW) is null
            ? GameHandoff.Swap
            : GameHandoff.EndThenStart;
    }

    /// <summary>
    /// The session's snapshot after a swap: the state it captured stays exactly
    /// as read before the first game (no new capture, R1); only the game, and the
    /// profile that decides what the final restore covers, become the new one's.
    /// What the exited game's profile changed was put back by the swap itself.
    /// </summary>
    public static PreGameSnapshot HandedOff(PreGameSnapshot pre, string executable, TuningProfile next)
    {
        ArgumentNullException.ThrowIfNull(pre);
        ArgumentNullException.ThrowIfNull(next);
        return pre with { Executable = executable, GameProfile = next };
    }

    /// <summary>
    /// The tray message after a swap, from what happened: the restore half and
    /// the apply half are both reported when either fell short.
    /// </summary>
    /// <param name="fansRestored">Null when the fans were not part of the restore half.</param>
    public static string DescribeHandoff(
        string exitedExecutable, GameRule next, ApplyResult restore, bool? fansRestored,
        IReadOnlyList<string> notRestored, ApplyResult applied)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(restore);
        ArgumentNullException.ThrowIfNull(notRestored);
        ArgumentNullException.ThrowIfNull(applied);
        string head = $"{exitedExecutable} exited — '{next.ProfileName}' for {next.ExecutableName}, still running, took over";
        var failedRestore = restore.Results.Where(r => !r.Applied).Select(r => r.Knob).ToList();
        if (fansRestored == false)
        {
            failedRestore.Add("fans");
        }

        var parts = new List<string>();
        if (failedRestore.Count > 0)
        {
            parts.Add($"putting back the pre-game {string.Join(", ", failedRestore)} failed");
        }

        if (notRestored.Count > 0)
        {
            parts.Add($"not put back: {string.Join("; ", notRestored)}");
        }

        if (!applied.AllSucceeded)
        {
            parts.Add("some of its knobs failed");
        }

        return parts.Count == 0 ? head + "." : $"{head}, but {string.Join("; ", parts)}.";
    }

    /// <summary>
    /// Puts one card's fans back into a captured configuration, through that
    /// card's own fan service. Returns whether the driver accepted it.
    /// </summary>
    public static bool ApplyFans(FanControlService fanService, FanRestoreState fans)
    {
        ArgumentNullException.ThrowIfNull(fanService);
        ArgumentNullException.ThrowIfNull(fans);
        switch (fans.Mode)
        {
            case FanMode.Fixed:
                return fanService.SetFixed(fans.FixedPct);
            case FanMode.Curve:
                if (fans.Curve is null)
                {
                    return false; // never falls through to a release nobody asked for
                }

                try
                {
                    fanService.SetCurve(fans.Curve);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }

            default:
                return fanService.SetAuto();
        }
    }

    /// <summary>
    /// The tray message after the restore — derived from what actually
    /// happened, so a partial restore is never reported as a restore.
    /// </summary>
    /// <param name="fansRestored">Null when the fans were not part of the restore.</param>
    public static string Describe(string executable, ApplyResult result, bool? fansRestored, IReadOnlyList<string> notRestored)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(notRestored);
        var failed = result.Results.Where(r => !r.Applied).Select(r => r.Knob).ToList();
        if (fansRestored == false)
        {
            failed.Add("fans");
        }

        if (failed.Count == 0 && notRestored.Count == 0)
        {
            return $"{executable} exited — tuning from before the game restored" +
                (fansRestored == true ? " (including fans)." : ".");
        }

        var parts = new List<string>();
        if (failed.Count > 0)
        {
            parts.Add($"failed: {string.Join(", ", failed)}");
        }

        if (notRestored.Count > 0)
        {
            parts.Add($"not restored: {string.Join("; ", notRestored)}");
        }

        return $"{executable} exited — the restore is incomplete ({string.Join(". ", parts)}).";
    }

    private static TuningProfile WithIfValid(
        PreGameSnapshot pre, TuningProfile current, TuningProfile candidate, List<string> notRestored, string whyNot)
    {
        if (candidate.Validate(pre.LockFloorMHz, pre.PowerFloorW) is null)
        {
            return candidate;
        }

        notRestored.Add(whyNot);
        return current;
    }

    private static string Fmt(double value) => value.ToString("F0", CultureInfo.InvariantCulture);
}
