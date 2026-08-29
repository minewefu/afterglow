using System.Globalization;
using Afterglow.Core.Hardware;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Tuning;

namespace Afterglow.Cli;

/// <summary>
/// `vfpoints [--gpu N]` — per-point V/F curve control (verified on RTX 50;
/// expected on RTX 20/30/40 via the same interfaces).
///   (no args)                     list the stored table with applied offsets
///   --set "IDX=MHZ,IDX=MHZ"       write specific point offsets
///   --flatten MV:MHZ              classic curve undervolt at the point nearest MV
///   --clear                       zero every point offset (incl. the global core offset)
/// </summary>
internal static class VfPointsCommand
{
    public static int Run(string[] args)
    {
        using var manager = new GpuManager();
        if (manager.Gpus.Count == 0)
        {
            Console.Error.WriteLine($"No NVIDIA GPU available (NVML: {manager.NvmlStatus}).");
            return 1;
        }

        uint gpuIndex = CliGpu.ParseIndex(args) ?? manager.Gpus[0].Index;
        var gpu = manager.Gpus.FirstOrDefault(g => g.Index == gpuIndex);
        if (gpu is null)
        {
            Console.Error.WriteLine($"GPU {gpuIndex} not found — {manager.Gpus.Count} NVIDIA GPU(s) detected.");
            return 2;
        }

        if (!gpu.Tuner.Capabilities.SupportsVfPoints)
        {
            Console.Error.WriteLine(
                $"GPU {gpu.Index} ({gpu.Name}): the driver did not expose per-point curve control " +
                "(the clock-boost interfaces answered nothing here — lock+offset undervolting still works).");
            return 1;
        }

        string? setArg = ValueOf(args, "--set");
        string? flattenArg = ValueOf(args, "--flatten");
        bool clear = args.Contains("--clear");

        if (clear)
        {
            var result = gpu.Tuner.ClearVfPointOffsets();
            Console.WriteLine($"{result.Knob}: {(result.Applied ? "ok" : "FAILED")} — {result.Detail}");
            return result.Applied ? 0 : 1;
        }

        if (setArg is not null)
        {
            var offsets = new Dictionary<int, int>();
            foreach (string pair in setArg.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = pair.Split('=');
                if (parts.Length != 2 ||
                    !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset))
                {
                    Console.Error.WriteLine($"Bad --set entry '{pair}' (expected IDX=MHZ, e.g. 120=-90).");
                    return 2;
                }

                offsets[index] = offset;
            }

            var result = gpu.Tuner.SetVfPointOffsets(offsets);
            Console.WriteLine($"{result.Knob}: {(result.Applied ? "ok" : "FAILED")} — {result.Detail}");
            return result.Applied ? 0 : 1;
        }

        if (flattenArg is not null)
        {
            string[] parts = flattenArg.Split(':');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double mv) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz))
            {
                Console.Error.WriteLine("Bad --flatten value (expected MV:MHZ, e.g. 875:1875).");
                return 2;
            }

            if (gpu.Tuner.TryReadVfPoints(out var points) != NvapiStatus.Ok)
            {
                Console.Error.WriteLine("Could not read the stored V/F table.");
                return 1;
            }

            var plan = VfPointPlanner.PlanFlatten(points, mv, mhz, out string? refusal);
            if (plan is null)
            {
                Console.Error.WriteLine(refusal);
                return 1;
            }

            Console.WriteLine(FormattableString.Invariant(
                $"Flatten @ {plan.AnchorVoltageMv:F0} mV: {plan.AnchorStoredClockMHz:F0} -> {plan.TargetClockMHz:F0} MHz, {plan.PointsFlattened} higher points capped."));
            var result = gpu.Tuner.SetVfPointOffsets(plan.OffsetsMHz);
            Console.WriteLine($"{result.Knob}: {(result.Applied ? "ok" : "FAILED")} — {result.Detail}");
            return result.Applied ? 0 : 1;
        }

        var rc = gpu.Tuner.TryReadVfPoints(out var table);
        if (rc != NvapiStatus.Ok)
        {
            Console.Error.WriteLine($"Reading the V/F table failed: {rc}");
            return 1;
        }

        Console.WriteLine($"GPU {gpu.Index} ({gpu.Name}) — {table.Count} core V/F points (slot: mV -> stored MHz, offset):");
        foreach (var point in table)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"  {point.Index,3}: {point.VoltageMv,6:F0} mV -> {point.ClockMHz,6:F0} MHz  {point.OffsetMHz,5:+0;-0;0} MHz"));
        }

        return 0;
    }

    private static string? ValueOf(string[] args, string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
