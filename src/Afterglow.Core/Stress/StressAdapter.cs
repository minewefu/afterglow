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
public static partial class StressAdapter
{
    public const uint NvidiaVendorId = 0x10DE;
    public const uint IntelVendorId = 0x8086;

    /// <summary>
    /// Marker embedded in the adapter description when the adapter was chosen
    /// without a PCI bus to bind to and more than one candidate existed — i.e.
    /// the pick is a guess. Unbound stress runs accept that; anything that
    /// attributes a RESULT to a specific card (certification above all) must not.
    /// </summary>
    public const string UnboundGuessMarker = "unverified card";

    /// <summary>True when a description from <see cref="Select"/> names a guessed adapter.</summary>
    public static bool IsUnboundGuess(string description) =>
        description.Contains(UnboundGuessMarker, StringComparison.Ordinal);

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
    /// <paramref name="description"/>) unless exactly one adapter of that vendor
    /// exists AND its bus query failed — then nothing contradicts the request,
    /// so it is used with a "bus unverified" note instead of making every stress
    /// test unusable on systems where the query fails. A lone adapter that
    /// reports a different bus is refused: the driver can see a card DXGI does
    /// not, so "only one adapter" does not mean "the right adapter" (see
    /// <see cref="Choose"/>).
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
            int pick = Choose(
                candidates.Select(c => new AdapterCandidate(c.Name, c.Vram, c.Bus)).ToList(),
                pciBusId,
                vendorName,
                out description);
            if (pick < 0)
            {
                return null;
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

    /// <summary>One enumerated adapter of the target vendor, as the selection policy sees it (Bus null = query failed).</summary>
    internal readonly record struct AdapterCandidate(string Name, long Vram, uint? Bus);

    /// <summary>
    /// The selection policy, separated from DXGI enumeration so it can be
    /// tested: the index of the adapter to load, or -1 with the reason in
    /// <paramref name="description"/>. A requested bus that no adapter is on
    /// is refused, with one exception — a lone adapter whose bus query failed,
    /// because then nothing contradicts the request. A lone adapter whose
    /// query answered with a DIFFERENT bus is not that exception: the answer
    /// says it is another card (the tuned one is hidden from DXGI — TCC or
    /// compute mode, say), and loading it would gather stress, VRAM, stepper
    /// and certification evidence from a card nobody tuned (F-17, F-62).
    /// </summary>
    internal static int Choose(IReadOnlyList<AdapterCandidate> candidates, uint? pciBusId, out string description) =>
        Choose(candidates, pciBusId, "NVIDIA", out description);

    /// <inheritdoc cref="Choose(IReadOnlyList{AdapterCandidate}, uint?, out string)"/>
    /// <param name="vendorName">The vendor named in the reason ("NVIDIA", "Intel").</param>
    internal static int Choose(
        IReadOnlyList<AdapterCandidate> candidates, uint? pciBusId, string vendorName, out string description)
    {
        description = string.Empty;
        if (candidates.Count == 0)
        {
            return -1;
        }

        int pick;
        if (pciBusId is { } bus)
        {
            pick = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Bus == bus)
                {
                    pick = i;
                    break;
                }
            }

            if (pick >= 0)
            {
                description = $"{candidates[pick].Name} (PCI bus {bus})";
            }
            else if (candidates.Count == 1 && candidates[0].Bus is null)
            {
                pick = 0;
                description = $"{candidates[0].Name} (bus unverified — DXGI bus query unavailable)";
            }
            else if (candidates.Count == 1)
            {
                description = $"the only {vendorName} adapter DirectX exposes ({candidates[0].Name}) is on PCI bus " +
                    $"{candidates[0].Bus}, not the tuned card's bus {bus} — refusing to load a different card.";
                return -1;
            }
            else
            {
                description = $"none of the {candidates.Count} {vendorName} adapters matched PCI bus {bus} — " +
                    "refusing to guess which card to load.";
                return -1;
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

            // Historical unbound behaviour (largest VRAM), preserved exactly.
            // The name is flagged as a guess when it WAS one, so callers that
            // must not guess — certification above all — can refuse.
            description = candidates.Count > 1
                ? $"{candidates[pick].Name} ({UnboundGuessMarker}: {candidates.Count} {vendorName} adapters, no PCI bus to bind to)"
                : candidates[pick].Name;
        }

        return pick;
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
            var adapters = new List<AdapterVendor>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
            {
                using (adapter)
                {
                    var desc = adapter.Description1;
                    adapters.Add(new AdapterVendor((uint)desc.VendorId, (desc.Flags & AdapterFlags.Software) != 0));
                }
            }

            return ChooseDefaultVendor(
                adapters, () => NvidiaPresentOnPciBus(CM_Get_Device_ID_List_SizeW, CM_Get_Device_ID_ListW));
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or DllNotFoundException)
        {
            // The engine worker will hit (and cleanly report) the same DXGI
            // failure through its historical NVIDIA path.
            return NvidiaVendorId;
        }
    }

    /// <summary>One DXGI adapter as the default-vendor policy sees it (Software = WARP / Basic Render Driver).</summary>
    internal readonly record struct AdapterVendor(uint VendorId, bool Software);

    /// <summary>
    /// The default-vendor policy of <see cref="DetectDefaultVendor"/>, separated
    /// from DXGI and the PnP query so it can be tested without this machine's
    /// devices. <paramref name="nvidiaPresentOnPciBus"/> is only asked when
    /// DXGI shows Intel as the sole hardware vendor.
    /// </summary>
    internal static uint ChooseDefaultVendor(IEnumerable<AdapterVendor> adapters, Func<bool> nvidiaPresentOnPciBus)
    {
        bool intelSeen = false;
        bool otherHardwareSeen = false;
        foreach (var adapter in adapters)
        {
            if (adapter.Software)
            {
                continue;
            }

            if (adapter.VendorId == NvidiaVendorId)
            {
                return NvidiaVendorId;
            }

            intelSeen |= adapter.VendorId == IntelVendorId;
            otherHardwareSeen |= adapter.VendorId != IntelVendorId;
        }

        return intelSeen && !otherHardwareSeen && !nvidiaPresentOnPciBus()
            ? IntelVendorId
            : NvidiaVendorId;
    }

    // cfgmgr32.h (Windows SDK 10.0.26100): CM_GETIDLIST_FILTER_ENUMERATOR, CM_GETIDLIST_FILTER_PRESENT.
    internal const uint CmGetIdListFilterEnumerator = 0x1;
    internal const uint CmGetIdListFilterPresent = 0x100;

    /// <summary>
    /// The flags both configuration-manager calls pass: PCI-enumerated devices
    /// that are present. Without FILTER_PRESENT the list includes device nodes
    /// PnP only remembers — a card swapped out, an unplugged eGPU — and one old
    /// NVIDIA node made an unbound run refuse the working Intel adapter (R-42).
    /// A disabled or driver-failed card is still present, so it still counts.
    /// The size and list calls must pass the same flags, or the size does not
    /// describe the list.
    /// </summary>
    internal const uint PciDeviceListFlags = CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;

    /// <summary>CM_Get_Device_ID_List_SizeW's shape, so tests can stand in a device tree.</summary>
    internal delegate int DeviceIdListSize(out uint length, string filter, uint flags);

    /// <summary>CM_Get_Device_ID_ListW's shape, so tests can stand in a device tree.</summary>
    internal delegate int DeviceIdList(string filter, char[] buffer, uint bufferLength, uint flags);

    /// <summary>
    /// True when any NVIDIA PCI device is present per the PnP device list —
    /// a disabled or driver-failed card is still listed there even when DXGI
    /// no longer enumerates it, and an unbound run must refuse loudly rather
    /// than silently retarget the iGPU in that state.
    /// </summary>
    internal static bool NvidiaPresentOnPciBus(DeviceIdListSize getSize, DeviceIdList getList)
    {
        try
        {
            if (getSize(out uint length, "PCI", PciDeviceListFlags) != 0 || length == 0)
            {
                return false;
            }

            var buffer = new char[length];
            if (getList("PCI", buffer, length, PciDeviceListFlags) != 0)
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

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint bufferLength, uint flags);

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
    internal struct KmtOpenAdapterFromLuid
    {
        public uint LowPart;
        public int HighPart;
        public uint AdapterHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KmtAdapterAddress
    {
        public uint BusNumber;
        public uint DeviceNumber;
        public uint FunctionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KmtQueryAdapterInfo
    {
        public uint AdapterHandle;
        public uint Type;
        public nint PrivateData;
        public uint PrivateDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KmtCloseAdapter
    {
        public uint AdapterHandle;
    }

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTOpenAdapterFromLuid(ref KmtOpenAdapterFromLuid adapter);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryAdapterInfo(ref KmtQueryAdapterInfo query);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTCloseAdapter(ref KmtCloseAdapter adapter);
}
