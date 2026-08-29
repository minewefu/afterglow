using System.Globalization;
using Afterglow.Core.Hardware;

namespace Afterglow.Cli;

/// <summary>Shared `--gpu N` handling for commands that bind work to one card.</summary>
internal static class CliGpu
{
    public static uint? ParseIndex(string[] args)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--gpu" &&
                uint.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint g))
            {
                return g;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves `--gpu N` to that GPU's PCI bus so the D3D stress engines bind
    /// to the exact card NVML tunes. Returns (null, null) when no --gpu was
    /// given — the engines then fall back to the largest NVIDIA adapter, which
    /// needs no NVML at all.
    /// </summary>
    public static (uint? Bus, string? Error) ResolveBus(string[] args)
    {
        if (ParseIndex(args) is not { } index)
        {
            return (null, null);
        }

        using var manager = new GpuManager();
        var gpu = manager.Gpus.FirstOrDefault(g => g.Index == index);
        if (gpu is null)
        {
            return (null, $"GPU {index} not found — {manager.Gpus.Count} NVIDIA GPU(s) detected.");
        }

        if (gpu.PciBusId is null)
        {
            return (null, $"GPU {index} ({gpu.Name}) did not report a PCI bus id; cannot bind the test to it.");
        }

        return (gpu.PciBusId, null);
    }
}
