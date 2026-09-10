using System.Globalization;
using Afterglow.Core.Hardware;

namespace Afterglow.Cli;

/// <summary>Shared `--gpu N` handling for commands that bind work to one card.</summary>
internal static class CliGpu
{
    /// <summary>
    /// Reads `--gpu N`. Returns null when it was not given AND when it was given
    /// with a value that will not parse — use <see cref="TryParseIndex"/> to tell
    /// those apart; every caller that binds work to a card must.
    /// </summary>
    public static uint? ParseIndex(string[] args) =>
        TryParseIndex(args, out uint? index, out _) ? index : null;

    /// <summary>
    /// Tri-state `--gpu` parse: absent, parsed, or present-but-unusable.
    /// <para>
    /// Collapsing the third case into "absent" was silent and dangerous. On a
    /// two-card machine `stress --gpu 1x` set <c>AllowUnboundGuess</c> — because
    /// the resolved bus was null — which disables the engine's own "cannot tell
    /// which card this would burn, refusing" guard, so it burned the largest-VRAM
    /// adapter and exited 0, documented as "stable", for a card the run never
    /// touched. `certify --gpu 1x` went further: it APPLIED the profile to GPU 0
    /// and stamped it certified. `mcp --gpu 1x` bound the whole agent-facing
    /// server to GPU 0. `--gpu 9` was refused; `--gpu 9x` was not.
    /// </para>
    /// </summary>
    /// <returns>False when the value is present but unparseable; <paramref name="error"/> then says so.</returns>
    public static bool TryParseIndex(string[] args, out uint? index, out string? error)
    {
        index = null;
        error = null;

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

            if (i + 1 >= args.Length)
            {
                error = "'--gpu' needs a GPU index (a whole number); none was given.";
                return false;
            }

            if (!uint.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint g))
            {
                error = $"'--gpu' needs a GPU index (a whole number), not '{args[i + 1]}'.";
                return false;
            }

            index = g;
            return true;
        }

        return true;
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
    /// needed), otherwise Intel so an Intel-only machine tests its own GPU.
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
            return (null, Core.Stress.StressAdapter.DetectDefaultVendor(), null);
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
}
