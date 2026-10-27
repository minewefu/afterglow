using System.Globalization;
using Afterglow.Core.Hardware;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Tuning;

namespace Afterglow.Cli;

/// <summary>
/// `vfpoints [--gpu N]` — per-point V/F curve control. The capability is
/// probed live, never assumed by generation: read and write verified on
/// RTX 5090 / driver 616.56; RTX 20/30/40 answer the same interfaces.
///   (no args)                     list the stored table with applied offsets
///   --set "IDX=MHZ,IDX=MHZ"       write specific point offsets (refused, never
///                                 clamped, outside ±1500 MHz or off the live table)
///   --flatten MV:MHZ              classic curve undervolt at the point nearest MV
///   --clear                       remove every per-point offset, then read the table back:
///                                 the output says whether shape is gone and what every
///                                 point reads now. On RTX 5090 / 616.56 that was the
///                                 global core offset — the clear left it in place (F-52)
/// Arguments are checked before the GPU is opened: an unknown option, a flag
/// missing its value, or more than one action is a usage error (exit 2), never
/// a silent fall-through to the read-only listing (F-12).
/// </summary>
internal static class VfPointsCommand
{
    internal enum VfPointsAction
    {
        List,
        Set,
        Flatten,
        Clear,
    }

    /// <summary>A fully parsed, range-checked `vfpoints` invocation.</summary>
    internal sealed record VfPointsRequest(
        uint? GpuIndex,
        VfPointsAction Action,
        IReadOnlyDictionary<int, int>? Offsets = null,
        double FlattenVoltageMv = 0,
        double FlattenClockMHz = 0);

    private const string Usage =
        "Usage: afterglow-cli vfpoints [--gpu N] [--set \"IDX=MHZ,...\" | --flatten MV:MHZ | --clear]";

    public static int Run(string[] args)
    {
        // Parse is the stricter check (a valueless --set/--flatten, a repeated or
        // conflicting action, a malformed --gpu — F-09, F-12) and gives the more
        // specific message, so it runs first; the shared option table must still
        // accept whatever Parse accepted, so it is checked too.
        if (Parse(args, out string? error) is not { } request)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (CliArgs.Validate(args, "vfpoints") is string argError)
        {
            Console.Error.WriteLine(argError);
            return 2;
        }

        using var manager = new GpuManager();
        if (manager.Gpus.Count == 0)
        {
            Console.Error.WriteLine($"No supported GPU available (NVML: {manager.NvmlStatus}, IGCL: {manager.IgclStatus}).");
            return 1;
        }

        // --gpu was parsed strictly by Parse (CliGpu.TryParseIndex) before the GPU
        // stack was opened; null means none was given.
        uint gpuIndex = request.GpuIndex ?? manager.Gpus[0].Index;
        var gpu = manager.Gpus.FirstOrDefault(g => g.Index == gpuIndex);
        if (gpu is null)
        {
            Console.Error.WriteLine($"GPU {gpuIndex} not found — {manager.Gpus.Count} GPU(s) detected.");
            return 2;
        }

        if (!gpu.Tuner.Capabilities.SupportsVfPoints)
        {
            Console.Error.WriteLine(
                $"GPU {gpu.Index} ({gpu.Name}): the driver did not expose per-point curve control " +
                "(the clock-boost interfaces answered nothing here — lock+offset undervolting still works).");
            return 1;
        }

        switch (request.Action)
        {
            case VfPointsAction.Clear:
            {
                // Name the card a write lands on, every time: on a multi-GPU box
                // the output is the last chance to notice the wrong one.
                Console.WriteLine($"GPU {gpu.Index} ({gpu.Name}):");
                var result = gpu.Tuner.ClearVfPointOffsets();
                Console.WriteLine($"{result.Knob}: {(result.Applied ? "ok" : "FAILED")} — {result.Detail}");
                return result.Applied ? 0 : 1;
            }

            case VfPointsAction.Set:
            {
                Console.WriteLine($"GPU {gpu.Index} ({gpu.Name}):");

                // The write goes on record as pending (R-13); a completed
                // command vouches for its own card's record, as `set` does (R-15).
                var claim = AppliedStateStore.BeginCommand(gpu.Tuner.RecordKey);
                var result = gpu.Tuner.SetVfPointOffsets(request.Offsets!);
                claim.MarkClean();
                Console.WriteLine($"{result.Knob}: {(result.Applied ? "ok" : "FAILED")} — {result.Detail}");
                return result.Applied ? 0 : 1;
            }

            case VfPointsAction.Flatten:
            {
                if (gpu.Tuner.TryReadVfPoints(out var points) != NvapiStatus.Ok)
                {
                    Console.Error.WriteLine("Could not read the stored V/F table.");
                    return 1;
                }

                var plan = VfPointPlanner.PlanFlatten(
                    points, request.FlattenVoltageMv, request.FlattenClockMHz, out string? refusal);
                if (plan is null)
                {
                    Console.Error.WriteLine(refusal);
                    return 1;
                }

                Console.WriteLine($"GPU {gpu.Index} ({gpu.Name}):");
                Console.WriteLine(FormattableString.Invariant(
                    $"Flatten @ {plan.AnchorVoltageMv:F0} mV: {plan.AnchorStoredClockMHz:F0} -> {plan.TargetClockMHz:F0} MHz, {plan.PointsFlattened} higher points capped") +
                    (plan.LowerPointsCapped > 0
                        ? FormattableString.Invariant($", {plan.LowerPointsCapped} lower points brought down to the target.")
                        : "."));
                var claim = AppliedStateStore.BeginCommand(gpu.Tuner.RecordKey); // as for --set (R-13, R-15)
                var result = gpu.Tuner.SetVfPointOffsets(plan.OffsetsMHz);
                claim.MarkClean();
                Console.WriteLine($"{result.Knob}: {(result.Applied ? "ok" : "FAILED")} — {result.Detail}");
                return result.Applied ? 0 : 1;
            }

            default:
                return List(gpu);
        }
    }

    private static int List(GpuContext gpu)
    {
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

    /// <summary>
    /// Parses and range-checks the whole command line without touching the
    /// GPU. Returns null with a reason for anything that is not exactly one
    /// well-formed request — including the valueless `--set` / `--flatten`
    /// that used to fall through to the listing and exit 0 (F-12), and a
    /// malformed `--gpu` that used to mean GPU 0 (F-09).
    /// </summary>
    internal static VfPointsRequest? Parse(string[] args, out string? error)
    {
        if (!CliGpu.TryParseIndex(args, out uint? gpuIndex, out error))
        {
            return null;
        }

        string? setArg = null;
        string? flattenArg = null;
        bool clear = false;
        int actions = 0;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--gpu":
                    i++; // value already validated by TryParseIndex
                    break;

                case "--set" or "--flatten":
                {
                    string flag = args[i];
                    // A value never starts with "--": that is the next option, i.e.
                    // the value was lost (quoting, a script variable that was empty).
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        error = flag == "--set"
                            ? "--set needs a value: --set \"IDX=MHZ,IDX=MHZ\" (e.g. --set \"120=-90\")."
                            : "--flatten needs a value: --flatten MV:MHZ (e.g. --flatten 875:1875).";
                        return null;
                    }

                    if ((flag == "--set" ? setArg : flattenArg) is not null)
                    {
                        error = $"{flag} was given more than once.";
                        return null;
                    }

                    if (flag == "--set")
                    {
                        setArg = args[++i];
                    }
                    else
                    {
                        flattenArg = args[++i];
                    }

                    actions++;
                    break;
                }

                case "--clear":
                    if (clear)
                    {
                        error = "--clear was given more than once.";
                        return null;
                    }

                    clear = true;
                    actions++;
                    break;

                default:
                    error = $"Unknown or malformed option '{args[i]}' for vfpoints.";
                    return null;
            }
        }

        if (actions > 1)
        {
            error = "Use one of --set, --flatten or --clear per run.";
            return null;
        }

        if (clear)
        {
            return new VfPointsRequest(gpuIndex, VfPointsAction.Clear);
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
                    error = $"Bad --set entry '{pair}' (expected IDX=MHZ, e.g. 120=-90).";
                    return null;
                }

                if (offsets.ContainsKey(index))
                {
                    error = $"Slot {index} appears more than once in --set.";
                    return null;
                }

                offsets[index] = offset;
            }

            // Refused, not clamped (F-10): "30=9000" is a typo, not a request
            // for the +1500 MHz the driver would have been handed instead.
            if (VfPointPlanner.ValidatePointOffsets(offsets, liveSlots: null) is string refusal)
            {
                error = refusal;
                return null;
            }

            return new VfPointsRequest(gpuIndex, VfPointsAction.Set, offsets);
        }

        if (flattenArg is not null)
        {
            string[] parts = flattenArg.Split(':');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double mv) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz))
            {
                error = "Bad --flatten value (expected MV:MHZ, e.g. 875:1875).";
                return null;
            }

            return new VfPointsRequest(gpuIndex, VfPointsAction.Flatten, FlattenVoltageMv: mv, FlattenClockMHz: mhz);
        }

        return new VfPointsRequest(gpuIndex, VfPointsAction.List);
    }
}
