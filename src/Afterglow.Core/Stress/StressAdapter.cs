using Vortice.DXGI;

namespace Afterglow.Core.Stress;

/// <summary>
/// Explicit D3D adapter selection for the stress engines. A null adapter in
/// D3D11CreateDevice takes whatever DXGI enumerates first — correct on most
/// desktops by luck, wrong on any machine where an iGPU drives the display.
/// The tests must load the GPU Afterglow is tuning, so we pick the NVIDIA
/// adapter (largest dedicated VRAM when several) and fail loudly when there
/// is none rather than silently burning a different card.
/// </summary>
internal static class StressAdapter
{
    private const uint NvidiaVendorId = 0x10DE;

    public static IDXGIAdapter1? SelectNvidia(out string description)
    {
        description = string.Empty;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        IDXGIAdapter1? best = null;
        long bestVram = -1;
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
        {
            var desc = adapter.Description1;
            long vram = (long)(ulong)desc.DedicatedVideoMemory;
            if ((uint)desc.VendorId == NvidiaVendorId && vram > bestVram)
            {
                best?.Dispose();
                best = adapter;
                bestVram = vram;
                description = desc.Description;
            }
            else
            {
                adapter.Dispose();
            }
        }

        return best;
    }
}
