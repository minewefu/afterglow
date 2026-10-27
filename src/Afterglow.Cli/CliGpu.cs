using System.Globalization;
using Afterglow.Core.Hardware;

namespace Afterglow.Cli;

/// <summary>Shared `--gpu N` handling for commands that bind work to one card.</summary>
internal static class CliGpu
{
    /// <summary>
    /// Tri-state `--gpu` parse: absent, parsed, or present-but-unusable (F-09).
    /// <para>
    /// Collapsing the third case into "absent" was silent and dangerous. Every
    /// caller read null as "use GPU 0" — so `--gpu --clear`, `--gpu one` or
    /// `--gpu -1` on a two-card box silently tuned the card the user did not
    /// name. On a two-card machine `stress --gpu 1x` set <c>AllowUnboundGuess</c>
    /// — because the resolved bus was null — which disables the engine's own
    /// "cannot tell which card this would burn, refusing" guard, so it burned the
    /// largest-VRAM adapter and exited 0, documented as "stable", for a card the
    /// run never touched. `certify --gpu 1x` went further: it APPLIED the profile
    /// to GPU 0 and stamped it certified. `mcp --gpu 1x` bound the whole
    /// agent-facing server to GPU 0. `--gpu 9` was refused; `--gpu 9x` was not.
    /// </para>
    /// </summary>
    /// <returns>
    /// True with <paramref name="index"/> null when no --gpu was given, true
    /// with the index when it parsed, false with <paramref name="error"/> when
    /// the flag is present but its value is missing, malformed, or repeated.
    /// </returns>
    public static bool TryParseIndex(string[] args, out uint? index, out string? error)
    {
        index = null;
        error = null;
        bool seen = false;

        // Scan to args.Length, not args.Length - 1: stopping one short made a
        // TRAILING `--gpu` invisible, so `set --core-offset 200 --gpu` read as
        // "no card requested" and wrote to GPU 0 — the same silent retarget the
        // tri-state parse exists to prevent, reached by a different route.
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] != "--gpu")
            {
                continue;
            }

            // A second --gpu is ambiguous, not "the last one wins".
            if (seen)
            {
                index = null;
                error = "--gpu was given more than once; name exactly one GPU index.";
                return false;
            }

            seen = true;
            if (i + 1 >= args.Length)
            {
                error = "--gpu needs a GPU index (e.g. --gpu 1); none was given.";
                return false;
            }

            // Digits only: a sign, a word or the next option is a typo, never "GPU 0".
            string value = args[i + 1];
            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed))
            {
                error = $"--gpu needs a GPU index (0, 1, ...); got '{value}'.";
                return false;
            }

            index = parsed;
            i++;
        }

        return true;
    }

    /// <summary>
    /// `--gpu N` for commands that resolve the card themselves: null when no
    /// --gpu was given, the index when it parsed. A present-but-unusable value
    /// is a usage error: the reason goes to stderr and the process exits with
    /// code 2 before any hardware is touched (the argparse convention), so no
    /// caller can fall back to GPU 0 on a typo. Callers that report the error
    /// themselves use <see cref="TryParseIndex"/> or <see cref="TryIndexOrFirst"/>.
    /// </summary>
    public static uint? ParseIndex(string[] args)
    {
        if (TryParseIndex(args, out uint? index, out string? error))
        {
            return index;
        }

        Console.Error.WriteLine(error);
        Environment.Exit(2);
        return null; // not reached
    }

    /// <summary>
    /// The index a command should act on: the requested one, or the first GPU
    /// when none was requested. Returns false — with the message to print and
    /// exit 2 — when `--gpu` was given a value that will not parse, so a
    /// malformed index can never silently become "GPU 0".
    /// </summary>
    public static bool TryIndexOrFirst(string[] args, uint firstIndex, out uint index, out string? error)
    {
        index = firstIndex;
        if (!TryParseIndex(args, out uint? parsed, out error))
        {
            return false;
        }

        index = parsed ?? firstIndex;
        return true;
    }

    /// <summary>
    /// Resolves `--gpu N` to that GPU's PCI bus and vendor so the D3D stress
    /// engines bind to the exact card being tuned. With no --gpu, the bus is
    /// null and the engines fall back to the target vendor's largest adapter —
    /// NVIDIA when one exists (the historical behavior, no driver stack
    /// needed), otherwise Intel so an Intel-only machine tests its own GPU,
    /// once Windows' device list confirms no NVIDIA card is present. When it
    /// can't (R2-D), the run is refused with the reason and how to name a card.
    /// </summary>
    public static (uint? Bus, uint VendorId, string? Error) ResolveTarget(string[] args)
    {
        if (!TryParseIndex(args, out uint? parsed, out string? parseError))
        {
            // An unusable --gpu is an error, never a fallback to "no particular
            // card": the fallback is exactly what let a named-card request become
            // an unbound guess.
            return (null, Core.Stress.StressAdapter.NvidiaVendorId, parseError);
        }

        if (parsed is not { } index)
        {
            var fallback = Core.Stress.StressAdapter.ResolveDefaultVendor();
            return (null, fallback.VendorId, fallback.Refusal is { } refusal ? UnboundRefusal(refusal) : null);
        }

        using var manager = new GpuManager();
        var gpu = manager.Gpus.FirstOrDefault(g => g.Index == index);
        if (gpu is null)
        {
            return (null, Core.Stress.StressAdapter.NvidiaVendorId,
                $"GPU {index} not found — {manager.Gpus.Count} GPU(s) detected.");
        }

        if (gpu.PciBusId is null)
        {
            return (null, gpu.PciVendorId,
                $"GPU {index} ({gpu.Name}) did not report a PCI bus id; cannot bind the test to it.");
        }

        return (gpu.PciBusId, gpu.PciVendorId, null);
    }

    /// <summary>
    /// A refused unbound `stress`/`vram` run: the reason, then the explicit way
    /// to pick the card. `--gpu N` binds by PCI vendor and bus through the GPU
    /// driver, so it never needs the device list that failed; `--probe-adapter`
    /// shows which index is which card without running a test.
    /// </summary>
    internal static string UnboundRefusal(string reason) =>
        $"{reason} Name the card to test with --gpu N; 'afterglow-cli stress --probe-adapter' lists each GPU's " +
        "index and the adapter it binds to, without running a test.";
}
