using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace Afterglow.Core.Stress;

/// <summary>
/// Explicit D3D adapter selection for the stress engines. A null adapter in
/// D3D11CreateDevice takes whatever DXGI enumerates first — correct on most
/// desktops by luck, wrong on any machine where an iGPU drives the display.
/// When the caller knows which card is being tuned, the adapter is bound by
/// vendor id plus PCI bus number (resolved from the DXGI adapter's LUID via
/// the documented D3DKMT kernel-thunk query) — the same key the NVML/NVAPI
/// pairing uses — so on a multi-GPU system the test can never land on the
/// wrong card because of enumeration order. Without a bus, it falls back to
/// the target vendor's adapter with the largest dedicated VRAM, and it fails
/// loudly rather than silently burning a different card.
/// </summary>
public static class StressAdapter
{
    public const uint NvidiaVendorId = 0x10DE;
    public const uint IntelVendorId = 0x8086;

    /// <summary>Legacy selection: the NVIDIA adapter with the most VRAM.</summary>
    public static IDXGIAdapter1? SelectNvidia(out string description) =>
        Select(null, out description);

    /// <summary>NVIDIA-vendor selection — the historical behavior, byte-for-byte.</summary>
    public static IDXGIAdapter1? Select(uint? pciBusId, out string description) =>
        Select(NvidiaVendorId, pciBusId, out description);

    /// <summary>
    /// Picks the adapter of the given PCI vendor on the given PCI bus; with a
    /// null bus, the vendor's largest-VRAM adapter. When a bus is requested but
    /// cannot be matched, this returns null (with the reason in
    /// <paramref name="description"/>) unless exactly one adapter of that
    /// vendor exists — a single card cannot be the wrong card, so it is used
    /// with a "bus unverified" note instead of making every stress test
    /// unusable on systems where the bus query fails.
    /// </summary>
    public static IDXGIAdapter1? Select(uint vendorId, uint? pciBusId, out string description)
    {
        description = string.Empty;
        string vendorName = VendorName(vendorId);
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        var candidates = new List<(IDXGIAdapter1 Adapter, string Name, long Vram, uint? Bus)>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
        {
            var desc = adapter.Description1;
            if ((uint)desc.VendorId != vendorId)
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
                    description = $"none of the {candidates.Count} {vendorName} adapters matched PCI bus {bus} — " +
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
    /// The vendor an unbound stress run should target: NVIDIA when any NVIDIA
    /// adapter exists (the historical largest-VRAM fallback), and Intel only
    /// when Intel is the sole hardware GPU vendor on the machine. Everything
    /// else returns NVIDIA so the historical loud refusal fires instead of a
    /// silent retarget — that covers AMD-plus-iGPU laptops, a disabled or
    /// TDR-dropped NVIDIA card that is absent from DXGI but still present on
    /// the PCI bus (checked via the documented cfgmgr32 device list), and any
    /// failure of the queries themselves. No driver stack is initialized.
    /// </summary>
    public static uint DetectDefaultVendor()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            bool intelSeen = false;
            bool otherHardwareSeen = false;
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
            {
                using (adapter)
                {
                    var desc = adapter.Description1;
                    if ((desc.Flags & AdapterFlags.Software) != 0)
                    {
                        continue; // WARP / Basic Render Driver
                    }

                    uint vendor = (uint)desc.VendorId;
                    if (vendor == NvidiaVendorId)
                    {
                        return NvidiaVendorId;
                    }

                    intelSeen |= vendor == IntelVendorId;
                    otherHardwareSeen |= vendor != IntelVendorId;
                }
            }

            return intelSeen && !otherHardwareSeen && !NvidiaPresentOnPciBus()
                ? IntelVendorId
                : NvidiaVendorId;
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or DllNotFoundException)
        {
            // The engine worker will hit (and cleanly report) the same DXGI
            // failure through its historical NVIDIA path.
            return NvidiaVendorId;
        }
    }

    /// <summary>
    /// True when any NVIDIA PCI device is present per the PnP device list —
    /// a disabled or driver-failed card is still listed there even when DXGI
    /// no longer enumerates it, and an unbound run must refuse loudly rather
    /// than silently retarget the iGPU in that state.
    /// </summary>
    private static bool NvidiaPresentOnPciBus()
    {
        try
        {
            const uint CmGetIdListFilterEnumerator = 0x1;
            if (CM_Get_Device_ID_List_SizeW(out uint length, "PCI", CmGetIdListFilterEnumerator) != 0 || length == 0)
            {
                return false;
            }

            var buffer = new char[length];
            if (CM_Get_Device_ID_ListW("PCI", buffer, length, CmGetIdListFilterEnumerator) != 0)
            {
                return false;
            }

            return new string(buffer).Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint bufferLength, uint flags);

    internal static string VendorName(uint vendorId) => vendorId switch
    {
        NvidiaVendorId => "NVIDIA",
        IntelVendorId => "Intel",
        _ => $"vendor 0x{vendorId:X4}",
    };

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
