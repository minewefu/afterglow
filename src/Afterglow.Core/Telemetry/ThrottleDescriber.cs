using Afterglow.Core.Interop.Nvml;

namespace Afterglow.Core.Telemetry;

/// <summary>
/// Turns the driver's throttle bitmask into plain language. GPU-Z shows "VRel/VOp/Pwr"
/// and leaves users guessing; Afterglow says what is actually happening.
/// </summary>
public static class ThrottleDescriber
{
    public sealed record ThrottleChip(string Label, string Explanation, ThrottleSeverity Severity);

    public enum ThrottleSeverity
    {
        Info,
        Expected,
        Warning,
        Critical,
    }

    public static IReadOnlyList<ThrottleChip> Describe(NvmlClocksEventReasons reasons)
    {
        var chips = new List<ThrottleChip>();

        if (reasons.HasFlag(NvmlClocksEventReasons.GpuIdle))
        {
            chips.Add(new("Idle", "Clocks are low because the GPU has nothing to do.", ThrottleSeverity.Info));
        }

        if (reasons.HasFlag(NvmlClocksEventReasons.SwPowerCap))
        {
            chips.Add(new("Power limit", "The board hit its power limit and lowered clocks to stay inside it. Raising the power limit (or undervolting) recovers clocks.", ThrottleSeverity.Expected));
        }

        if (reasons.HasFlag(NvmlClocksEventReasons.SwThermalSlowdown))
        {
            chips.Add(new("Thermal limit", "The GPU reached its temperature target and is reducing clocks. Better airflow, a more aggressive fan curve, or a lower power limit helps.", ThrottleSeverity.Warning));
        }

        if (reasons.HasFlag(NvmlClocksEventReasons.HwThermalSlowdown))
        {
            chips.Add(new("HW thermal brake", "Emergency hardware thermal slowdown (~half clocks). The GPU is critically hot — check cooling now.", ThrottleSeverity.Critical));
        }

        if (reasons.HasFlag(NvmlClocksEventReasons.HwPowerBrakeSlowdown))
        {
            chips.Add(new("Power brake", "The power supply asserted an emergency brake (external power event). Check PSU cables and capacity.", ThrottleSeverity.Critical));
        }

        if (reasons.HasFlag(NvmlClocksEventReasons.HwSlowdown))
        {
            chips.Add(new("HW slowdown", "A hardware slowdown signal is active (thermal or power brake).", ThrottleSeverity.Warning));
        }

        if (reasons.HasFlag(NvmlClocksEventReasons.ApplicationsClocksSetting))
        {
            chips.Add(new("Clock setting", "Clocks are capped by an applications-clock or locked-clock setting (e.g., Afterglow's clock lock).", ThrottleSeverity.Info));
        }

        if (reasons.HasFlag(NvmlClocksEventReasons.SyncBoost))
        {
            chips.Add(new("Sync boost", "Clocks are matched to another GPU in a sync-boost group.", ThrottleSeverity.Info));
        }

        if (reasons.HasFlag(NvmlClocksEventReasons.DisplayClockSetting))
        {
            chips.Add(new("Display setting", "A display-related clock constraint is active.", ThrottleSeverity.Info));
        }

        // Newer drivers report bits this build does not know yet (616.xx sets
        // 0x400 in ordinary operation). An unknown reason must never be
        // indistinguishable from "not throttling" — surface it raw instead.
        const NvmlClocksEventReasons known =
            NvmlClocksEventReasons.GpuIdle |
            NvmlClocksEventReasons.ApplicationsClocksSetting |
            NvmlClocksEventReasons.SwPowerCap |
            NvmlClocksEventReasons.HwSlowdown |
            NvmlClocksEventReasons.SyncBoost |
            NvmlClocksEventReasons.SwThermalSlowdown |
            NvmlClocksEventReasons.HwThermalSlowdown |
            NvmlClocksEventReasons.HwPowerBrakeSlowdown |
            NvmlClocksEventReasons.DisplayClockSetting;
        var unknown = reasons & ~known;
        if (unknown != 0)
        {
            chips.Add(new(
                $"Driver-reported (0x{(ulong)unknown:X})",
                $"The driver reports an additional clock-event reason (bitmask 0x{(ulong)unknown:X}) that this " +
                "Afterglow build does not decode yet. Shown raw rather than hidden.",
                ThrottleSeverity.Info));
        }

        return chips;
    }
}
