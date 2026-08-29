using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace Afterglow.Core.Stress;

/// <summary>
/// Explicit D3D adapter selection for the stress engines. A null adapter in
/// D3D11CreateDevice takes whatever DXGI enumerates first — correct on most
/// desktops by luck, wrong on any machine where an iGPU drives the display.
/// When the caller knows which card is being tuned, the adapter is bound by
/// PCI bus number (resolved from the DXGI adapter's LUID via the documented
/// D3DKMT kernel-thunk query) — the same key the NVML/NVAPI pairing uses —
/// so on a multi-NVIDIA-GPU system the test can never land on the wrong
/// card because of enumeration order. Without a bus, it falls back to the
/// NVIDIA adapter with the largest dedicated VRAM, and it fails loudly
/// rather than silently burning a different card.
/// </summary>
public static class StressAdapter
{
    private const uint NvidiaVendorId = 0x10DE;

    /// <summary>Legacy selection: the NVIDIA adapter with the most VRAM.</summary>
    public static IDXGIAdapter1? SelectNvidia(out string description) =>
        Select(null, out description);

    /// <summary>
    /// Picks the NVIDIA adapter on the given PCI bus; with a null bus, the
    /// largest-VRAM NVIDIA adapter. When a bus is requested but cannot be
    /// matched, this returns null (with the reason in <paramref name="description"/>)
    /// unless exactly one NVIDIA adapter exists — a single card cannot be the
    /// wrong card, so it is used with a "bus unverified" note instead of
    /// making every stress test unusable on systems where the bus query fails.
    /// </summary>
    public static IDXGIAdapter1? Select(uint? pciBusId, out string description)
    {
        description = string.Empty;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        var candidates = new List<(IDXGIAdapter1 Adapter, string Name, long Vram, uint? Bus)>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
        {
            var desc = adapter.Description1;
            if ((uint)desc.VendorId != NvidiaVendorId)
            {
                adapter.Dispose();
                continue;
            }

            candidates.Add((adapter, desc.Description, (long)(ulong)desc.DedicatedVideoMemory,
                TryGetBusNumber(desc.Luid)));
        }

        try
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            int pick;
            if (pciBusId is { } bus)
            {
                pick = candidates.FindIndex(c => c.Bus == bus);
                if (pick >= 0)
                {
                    description = $"{candidates[pick].Name} (PCI bus {bus})";
                }
                else if (candidates.Count == 1)
                {
                    pick = 0;
                    description = $"{candidates[0].Name} (bus unverified — DXGI bus query unavailable)";
                }
                else
                {
                    description = $"none of the {candidates.Count} NVIDIA adapters matched PCI bus {bus} — " +
                        "refusing to guess which card to load.";
                    return null;
                }
            }
            else
            {
                pick = 0;
                for (int i = 1; i < candidates.Count; i++)
                {
                    if (candidates[i].Vram > candidates[pick].Vram)
                    {
                        pick = i;
                    }
                }

                description = candidates[pick].Name;
            }

            var chosen = candidates[pick].Adapter;
            candidates.RemoveAt(pick);
            return chosen;
        }
        finally
        {
            foreach (var (adapter, _, _, _) in candidates)
            {
                adapter.Dispose();
            }
        }
    }

    /// <summary>
    /// DXGI adapter LUID → PCI bus number via the D3DKMT adapter-address
    /// query (gdi32 kernel thunks; the mechanism hardware monitors use).
    /// Null when the query fails.
    /// </summary>
    private static uint? TryGetBusNumber(Vortice.Luid adapterLuid)
    {
        var open = new KmtOpenAdapterFromLuid
        {
            LowPart = (uint)adapterLuid.LowPart,
            HighPart = adapterLuid.HighPart,
        };
        if (D3DKMTOpenAdapterFromLuid(ref open) < 0)
        {
            return null;
        }

        try
        {
            var address = default(KmtAdapterAddress);
            unsafe
            {
                var query = new KmtQueryAdapterInfo
                {
                    AdapterHandle = open.AdapterHandle,
                    Type = KmtQueryAdapterAddress,
                    PrivateData = (nint)(&address),
                    PrivateDataSize = (uint)sizeof(KmtAdapterAddress),
                };
                if (D3DKMTQueryAdapterInfo(ref query) < 0)
                {
                    return null;
                }
            }

            return address.BusNumber;
        }
        finally
        {
            var close = new KmtCloseAdapter { AdapterHandle = open.AdapterHandle };
            _ = D3DKMTCloseAdapter(ref close);
        }
    }

    // KMTQAITYPE_ADAPTERADDRESS from d3dkmthk.h (UMDRIVERPRIVATE=0 … FLIPQUEUEINFO=5, ADAPTERADDRESS=6).
    private const uint KmtQueryAdapterAddress = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct KmtOpenAdapterFromLuid
    {
        public uint LowPart;
        public int HighPart;
        public uint AdapterHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KmtAdapterAddress
    {
        public uint BusNumber;
        public uint DeviceNumber;
        public uint FunctionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KmtQueryAdapterInfo
    {
        public uint AdapterHandle;
        public uint Type;
        public nint PrivateData;
        public uint PrivateDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KmtCloseAdapter
    {
        public uint AdapterHandle;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref KmtOpenAdapterFromLuid adapter);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref KmtQueryAdapterInfo query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref KmtCloseAdapter adapter);
}
