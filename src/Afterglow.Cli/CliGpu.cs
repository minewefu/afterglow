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
    /// Resolves `--gpu N` to that GPU's PCI bus and vendor so the D3D stress
    /// engines bind to the exact card being tuned. With no --gpu, the bus is
    /// null and the engines fall back to the target vendor's largest adapter —
    /// NVIDIA when one exists (the historical behavior, no driver stack
    /// needed), otherwise Intel so an Intel-only machine tests its own GPU.
    /// </summary>
    public static (uint? Bus, uint VendorId, string? Error) ResolveTarget(string[] args)
    {
        if (ParseIndex(args) is not { } index)
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
