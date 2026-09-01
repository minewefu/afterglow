using System.Globalization;
using Afterglow.Core.Hardware;
using Afterglow.Core.Profiles;
using Afterglow.Core.Tuning;

namespace Afterglow.Cli;

/// <summary>`caps`, `get`, `set`, `reset` — scriptable tuning.</summary>
internal static class TuneCommands
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOut = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int Caps(string[] args)
    {
        using var manager = new GpuManager();
        if (SelectGpu(manager, args) is not { } gpu)
        {
            return 1;
        }

        var c = gpu.Tuner.Capabilities;

        if (args.Contains("--json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                gpu = gpu.Name,
                index = gpu.Index,
                driver = gpu.DriverVersion,
                architecture = gpu.Architecture,
                capabilities = c,
            }, JsonOut));
            return 0;
        }

        // On Intel the all-false flags are Afterglow's not-implemented-yet
        // policy, not the driver's answer — say which one is speaking.
        Console.WriteLine(gpu.Vendor == GpuVendor.Intel
            ? $"{gpu.Name} (GPU {gpu.Index}) — Afterglow tuning capabilities (tuning not implemented for Intel GPUs in this beta):"
            : $"{gpu.Name} (GPU {gpu.Index}) — driver-reported tuning capabilities:");
        Console.WriteLine($"  Core offset     {(c.SupportsCoreOffset ? $"{c.CoreOffsetMinMHz}..{c.CoreOffsetMaxMHz} MHz" : "not supported")}");
        Console.WriteLine($"  Memory offset   {(c.SupportsMemOffset ? $"{c.MemOffsetMinMHz}..{c.MemOffsetMaxMHz} MHz" : "not supported")}");
        Console.WriteLine($"  Power limit     {(c.SupportsPowerLimit ? $"{c.PowerLimitMinW:F0}..{c.PowerLimitMaxW:F0} W (default {c.PowerLimitDefaultW:F0})" : "not supported")}");
        Console.WriteLine($"  Clock lock      {(c.SupportsLockedCoreClock ? $"up to {c.MaxCoreClockMHz} MHz" : "not supported")}");
        Console.WriteLine($"  Fans            {(c.SupportsFanControl ? $"{c.FanCount} fans, min spin {c.FanMinDutyPct}%" : "not supported")}");
        Console.WriteLine($"  Voltage boost   {(c.SupportsVoltageBoost ? "supported" : "not supported")}");
        Console.WriteLine($"  Temp limit      {(c.SupportsTempLimit ? $"{c.TempLimitMinC}..{c.TempLimitMaxC} C (default {c.TempLimitDefaultC})" : "not supported on this GPU/driver")}");
        return 0;
    }

    public static int Get(string[] args)
    {
        using var manager = new GpuManager();
        if (SelectGpu(manager, args) is not { } gpu)
        {
            return 1;
        }

        var (core, mem, power, boost, lockMHz) = gpu.Tuner.ReadCurrent();

        if (args.Contains("--json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                gpu = gpu.Name,
                core_offset_mhz = core,
                mem_offset_mhz = mem,
                power_limit_w = power,
                voltage_boost_pct = boost,
                lock_clock_mhz = lockMHz,
            }, JsonOut));
            return 0;
        }

        Console.WriteLine($"{gpu.Name} (GPU {gpu.Index}) — current applied state:");
        Console.WriteLine($"  Core offset     {core} MHz");
        Console.WriteLine($"  Memory offset   {mem} MHz");
        Console.WriteLine($"  Power limit     {(power is double p ? $"{p:F0} W" : "not supported")}");
        if (boost is uint b)
        {
            Console.WriteLine($"  Voltage boost   {b}%");
        }

        Console.WriteLine(lockMHz is uint lc
            ? $"  Clock lock      210..{lc} MHz (Afterglow-tracked; the driver has no getter)"
            : "  Clock lock      none");

        return 0;
    }

    public static int Set(string[] args)
    {
        int? coreOffset = null, memOffset = null;
        double? powerLimit = null;
        uint? lockClock = null, voltageBoost = null, tempLimit = null;
        bool unlock = false;
        string? fan = null;

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (arg)
            {
                case "--core-offset" when TryInt(next, out int v):
                    coreOffset = v;
                    i++;
                    break;
                case "--mem-offset" when TryInt(next, out int v):
                    memOffset = v;
                    i++;
                    break;
                case "--power-limit" when TryDouble(next, out double v):
                    powerLimit = v;
                    i++;
                    break;
                case "--lock-clock" when next == "off":
                    unlock = true;
                    i++;
                    break;
                case "--lock-clock" when TryInt(next, out int v) && v > 0:
                    lockClock = (uint)v;
                    i++;
                    break;
                case "--voltage-boost" when TryInt(next, out int v) && v >= 0:
                    voltageBoost = (uint)v;
                    i++;
                    break;
                case "--temp-limit" when TryInt(next, out int v) && v > 0:
                    tempLimit = (uint)v;
                    i++;
                    break;
                case "--fan" when next is not null:
                    fan = next;
                    i++;
                    break;
                case "--gpu":
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or malformed option '{arg}'.");
                    return 2;
            }
        }

        using var manager = new GpuManager();
        if (SelectGpu(manager, args) is not { } gpu)
        {
            return 1;
        }

        if (fan is not null && fan != "auto" && !uint.TryParse(fan, out _))
        {
            Console.Error.WriteLine("--fan expects 'auto' or a duty percentage.");
            return 2;
        }

        var current = gpu.Tuner.ReadCurrent();
        var profile = new TuningProfile
        {
            Name = "cli",
            CoreOffsetMHz = coreOffset ?? current.CoreOffsetMHz,
            MemOffsetMHz = memOffset ?? current.MemOffsetMHz,
            PowerLimitW = powerLimit,
            TempLimitC = tempLimit,
            // An unspecified --lock-clock preserves the currently tracked lock.
            LockedCoreClockMHz = unlock ? null : (lockClock ?? current.LockedCoreClockMHz),
            VoltageBoostPct = voltageBoost,
        };

        var result = gpu.Tuner.Apply(profile);
        bool allOk = result.AllSucceeded;
        foreach (var knob in result.Results)
        {
            Console.WriteLine($"  {(knob.Applied ? "ok  " : "FAIL")} {knob.Knob,-18} {knob.Detail}");
        }

        // Explicit `--lock-clock off` always issues the driver release, even when no
        // lock is tracked (one can outlive a crashed session until reboot).
        if (unlock)
        {
            var knob = gpu.Tuner.ForceUnlock();
            Console.WriteLine($"  {(knob.Applied ? "ok  " : "FAIL")} {knob.Knob,-18} {knob.Detail}");
            allOk &= knob.Applied;
        }

        // Fans are commanded directly (not part of profile apply).
        if (fan == "auto")
        {
            var rc = gpu.Tuner.RestoreAutoFansRaw();
            Console.WriteLine($"  {(rc == Core.Interop.Nvml.NvmlReturn.Success ? "ok  " : "FAIL")} {"fans",-18} auto");
            allOk &= rc == Core.Interop.Nvml.NvmlReturn.Success;
        }
        else if (fan is not null && uint.TryParse(fan, out uint requestedDuty))
        {
            uint duty = TuningMath.NormalizeFixedFanDuty(requestedDuty, gpu.Tuner.Capabilities.FanMinDutyPct);
            var rc = gpu.Tuner.SetAllFansRaw(duty);
            string detail = duty == requestedDuty
                ? $"{duty}% fixed"
                : $"{duty}% fixed (requested {requestedDuty}%, raised to the hardware minimum spin duty)";
            Console.WriteLine($"  {(rc == Core.Interop.Nvml.NvmlReturn.Success ? "ok  " : "FAIL")} {"fans",-18} {detail}");
            allOk &= rc == Core.Interop.Nvml.NvmlReturn.Success;
        }

        // A completed CLI apply is a clean session end — don't trip the app's
        // crash-recovery banner on its next start.
        AppliedStateStore.MarkCleanShutdown();

        if (!allOk)
        {
            Console.Error.WriteLine(gpu.Vendor == GpuVendor.Intel
                ? "Some knobs failed. Tuning is not implemented for Intel GPUs in this beta — monitoring only."
                : "Some knobs failed. Run elevated (administrator) for write access.");
            return 1;
        }

        return 0;
    }

    public static int Reset(string[] args)
    {
        using var manager = new GpuManager();
        if (SelectGpu(manager, args) is not { } gpu)
        {
            return 1;
        }

        var result = gpu.Tuner.ResetToDefaults();
        foreach (var knob in result.Results)
        {
            Console.WriteLine($"  {(knob.Applied ? "ok  " : "FAIL")} {knob.Knob,-18} {knob.Detail}");
        }

        return result.AllSucceeded ? 0 : 1;
    }

    private static GpuContext? SelectGpu(GpuManager manager, string[] args)
    {
        if (manager.Gpus.Count == 0)
        {
            Console.Error.WriteLine($"No supported GPU found (NVML: {manager.NvmlStatus}, IGCL: {manager.IgclStatus}).");
            return null;
        }

        uint index = 0;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--gpu" && uint.TryParse(args[i + 1], out uint g))
            {
                index = g;
            }
        }

        var gpu = manager.Gpus.FirstOrDefault(g => g.Index == index);
        if (gpu is null)
        {
            Console.Error.WriteLine($"GPU {index} not found ({manager.Gpus.Count} present).");
        }

        return gpu;
    }

    private static bool TryInt(string? s, out int value) =>
        int.TryParse(s, NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static bool TryDouble(string? s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
