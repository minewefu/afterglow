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
        if (CliArgs.Validate(args, "caps") is string argError)
        {
            Console.Error.WriteLine(argError);
            return 2;
        }

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

        // On Intel a flag means "Afterglow drives this knob on this device,
        // verified by readback" — a mix of driver answers and not-implemented-
        // yet policy, so don't label it as purely the driver speaking.
        Console.WriteLine(gpu.Vendor == GpuVendor.Intel
            ? $"{gpu.Name} (GPU {gpu.Index}) — knobs Afterglow can drive on this device (others read \"not supported\"):"
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
        if (CliArgs.Validate(args, "get") is string argError)
        {
            Console.Error.WriteLine(argError);
            return 2;
        }

        using var manager = new GpuManager();
        if (SelectGpu(manager, args) is not { } gpu)
        {
            return 1;
        }

        var (core, mem, power, boost, lockMHz) = gpu.Tuner.ReadCurrent();
        var caps = gpu.Tuner.Capabilities;

        // A knob this device does not expose has no "current value": printing 0
        // there is the same fabrication the power-limit slot was made nullable
        // to avoid, and it directly contradicts what `caps` says one line over.
        // NVIDIA supports both offsets, so its output is unchanged.
        int? coreOffset = caps.SupportsCoreOffset ? core : null;
        int? memOffset = caps.SupportsMemOffset ? mem : null;

        if (args.Contains("--json"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                gpu = gpu.Name,
                core_offset_mhz = coreOffset,
                mem_offset_mhz = memOffset,
                power_limit_w = power,
                voltage_boost_pct = boost,
                lock_clock_mhz = lockMHz,
            }, JsonOut));
            return 0;
        }

        Console.WriteLine($"{gpu.Name} (GPU {gpu.Index}) — current applied state:");
        Console.WriteLine($"  Core offset     {(coreOffset is int co ? $"{co} MHz" : "not supported")}");
        Console.WriteLine($"  Memory offset   {(memOffset is int mo ? $"{mo} MHz" : "not supported")}");
        Console.WriteLine($"  Power limit     {(power is double p ? $"{p:F0} W" : "not supported")}");
        if (boost is uint b)
        {
            Console.WriteLine($"  Voltage boost   {b}%");
        }

        // NVIDIA's lock has no driver getter (the value is Afterglow-tracked);
        // Intel's frequency clamp reads straight back from the driver.
        Console.WriteLine(lockMHz is uint lc
            ? gpu.Vendor == GpuVendor.Intel
                ? $"  Clock lock      clamped to {lc} MHz (read back from the driver)"
                : $"  Clock lock      210..{lc} MHz (Afterglow-tracked; the driver has no getter)"
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

        // Declared in CliArgs.Options like every other command, so the option
        // surface is covered by the contract test and a missing value is
        // reported by the shared checker rather than a hand-typed copy of it.
        if (CliArgs.Validate(args, "set") is string argError)
        {
            Console.Error.WriteLine(argError);
            return 2;
        }

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
                    i++; // value checked by CliArgs.Validate / CliGpu.TryParseIndex
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
            // An unspecified --lock-clock preserves the lock Afterglow APPLIED —
            // not ReadCurrent's element, which on Arc is a driver observation:
            // carrying a factory ceiling forward wrote it as a clamp with
            // written provenance that no release could ever adopt again.
            LockedCoreClockMHz = unlock ? null : (lockClock ?? gpu.Tuner.AppliedLockMHz),
            VoltageBoostPct = voltageBoost,
        };

        // An explicit `--lock-clock off` is a direct instruction, so issue the
        // driver release FIRST — including for a clamp this session did not
        // apply, which Apply deliberately declines to touch and reports as a
        // failure. Running it afterwards printed that refusal and then the
        // successful release for one user-requested operation, and exited 1 on a
        // release that worked. Releasing first also leaves nothing for Apply's
        // lock-less path to find, so it stays quiet.
        bool alreadyReleased = false;
        KnobResult? refusedRelease = null;
        if (unlock)
        {
            var unlockKnob = gpu.Tuner.ForceUnlock();
            if (unlockKnob.Applied)
            {
                Console.WriteLine($"  ok   {unlockKnob.Knob,-18} {unlockKnob.Detail}");
                alreadyReleased = true;
            }
            else
            {
                // A refused release leaves the clamp tracked, so Apply's
                // lock-less path retries it below; its knob line is the one
                // verdict. Printing this refusal too gave two FAIL lines — or a
                // FAIL followed by "ok … released (verified)" and exit 1.
                refusedRelease = unlockKnob;
            }
        }

        var result = gpu.Tuner.Apply(profile);
        bool applyReleased = result.Results.Any(k => k.Knob == "clock lock" && k.Applied);
        if (refusedRelease is { } refused && !result.Results.Any(k => k.Knob == "clock lock"))
        {
            // Apply found nothing to retry (nothing tracked), so the explicit
            // refusal is the only account of the release.
            Console.WriteLine($"  FAIL {refused.Knob,-18} {refused.Detail}");
        }

        bool allOk = result.AllSucceeded && (!unlock || alreadyReleased || applyReleased);
        foreach (var knob in result.Results)
        {
            // A bare `--lock-clock off` carries no other knob, so the engine's
            // "nothing in this profile applies" note is expected here and would
            // read as though the release had not happened. It still counts
            // toward the result; it just is not news worth printing.
            if (unlock && knob.Applied && knob.Knob == "profile")
            {
                continue;
            }

            Console.WriteLine($"  {(knob.Applied ? "ok  " : "FAIL")} {knob.Knob,-18} {knob.Detail}");
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
                ? "Some knobs failed. On Intel, Afterglow drives only the knobs 'caps' lists as available; if the driver refused one of those, run elevated (administrator) for write access."
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

        // Same rule as everywhere else: an unusable --gpu is an error, not a
        // silent write to GPU 0. This is the tuning-write path.
        if (!CliGpu.TryParseIndex(args, out uint? parsedIndex, out string? gpuArgError))
        {
            Console.Error.WriteLine(gpuArgError);
            return null;
        }

        uint index = parsedIndex ?? 0;

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
