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
    /// The vendor an unbound stress run targets, as <see cref="ResolveDefaultVendor"/>
    /// decides it, for callers that cannot show its refusal: a refused choice
    /// returns NVIDIA, so on a machine without an NVIDIA adapter the engines
    /// refuse ("No NVIDIA adapter found") rather than burn the Intel one.
    /// </summary>
    public static uint DetectDefaultVendor() => ResolveDefaultVendor().VendorId;

    /// <summary>
    /// The vendor an unbound stress run (no card named) should target: NVIDIA
    /// when DXGI shows an NVIDIA adapter (the historical largest-VRAM fallback),
    /// and Intel only when Intel is the sole hardware vendor in DXGI AND the
    /// documented cfgmgr32 PCI device list confirms no NVIDIA card is present.
    /// <para>
    /// The list is what catches a disabled or TDR-dropped NVIDIA card, which
    /// DXGI no longer shows. When it lists one, or when the list can't be read
    /// at all, the run is refused with a reason (R2-D): it used to treat a
    /// failed query as "no NVIDIA card" and test the Intel adapter. Every other
    /// case returns NVIDIA without a reason, so the engines' historical "No
    /// NVIDIA adapter found" refusal fires — AMD-plus-iGPU laptops, and a
    /// machine whose DXGI shows no hardware adapter. A DXGI failure also returns
    /// NVIDIA: the engine hits the same failure and reports it. No driver stack
    /// is initialized.
    /// </para>
    /// </summary>
    public static DefaultVendorChoice ResolveDefaultVendor()
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
            return DefaultVendorChoice.Vendor(NvidiaVendorId);
        }
    }

    /// <summary>
    /// What <see cref="ResolveDefaultVendor"/> decided: the vendor an unbound
    /// run targets, or — when <see cref="Refusal"/> is set — why no adapter can
    /// be picked without guessing. The reason says what was found, not what to
    /// do: each surface adds how to name a card there (CLI `--gpu N`). A refused
    /// choice still carries NVIDIA, so a caller that ignores the reason fails
    /// closed through the engines' "No NVIDIA adapter found" refusal.
    /// </summary>
    public readonly record struct DefaultVendorChoice(uint VendorId, string? Refusal)
    {
        public static DefaultVendorChoice Vendor(uint vendorId) => new(vendorId, null);

        public static DefaultVendorChoice Refused(string reason) => new(NvidiaVendorId, reason);
    }

    /// <summary>The reason given when the PCI device list can't be read (R2-D).</summary>
    internal const string DeviceListUnreadableReason =
        "No GPU was named, and Afterglow can't tell which one to test: DirectX shows Intel graphics and no NVIDIA " +
        "adapter, but Windows' list of PCI devices couldn't be read, so a disabled or driver-reset NVIDIA card " +
        "can't be ruled out. Testing the Intel adapter would be a guess, so the run is refused.";

    /// <summary>The reason given when the PCI device list shows an NVIDIA card DirectX doesn't.</summary>
    internal const string NvidiaHiddenFromDirectXReason =
        "No GPU was named, and Afterglow won't guess which one to test: Windows lists an NVIDIA card that " +
        "DirectX doesn't show (it may be disabled, or its driver may have failed or reset), so testing the " +
        "Intel adapter instead could stamp a result on the wrong card. The run is refused.";

    /// <summary>One DXGI adapter as the default-vendor policy sees it (Software = WARP / Basic Render Driver).</summary>
    internal readonly record struct AdapterVendor(uint VendorId, bool Software);

    /// <summary>
    /// The default-vendor policy of <see cref="ResolveDefaultVendor"/>, separated
    /// from DXGI and the PnP query so it can be tested without this machine's
    /// devices. <paramref name="nvidiaPresentOnPciBus"/> is only asked when
    /// DXGI shows Intel as the sole hardware vendor; null means the device list
    /// couldn't be read.
    /// </summary>
    internal static DefaultVendorChoice ChooseDefaultVendor(
        IEnumerable<AdapterVendor> adapters, Func<bool?> nvidiaPresentOnPciBus)
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
                return DefaultVendorChoice.Vendor(NvidiaVendorId);
            }

            intelSeen |= adapter.VendorId == IntelVendorId;
            otherHardwareSeen |= adapter.VendorId != IntelVendorId;
        }

        if (!intelSeen || otherHardwareSeen)
        {
            return DefaultVendorChoice.Vendor(NvidiaVendorId);
        }

        // Fail closed (R2-D): "couldn't ask" is not "no NVIDIA card". The doc
        // promised NVIDIA for a failed query, but a failed query returned false
        // and the run went to the Intel adapter.
        return nvidiaPresentOnPciBus() switch
        {
            false => DefaultVendorChoice.Vendor(IntelVendorId),
            true => DefaultVendorChoice.Refused(NvidiaHiddenFromDirectXReason),
            null => DefaultVendorChoice.Refused(DeviceListUnreadableReason),
        };
    }

    // cfgmgr32.h (Windows SDK 10.0.26100): CM_GETIDLIST_FILTER_ENUMERATOR, CM_GETIDLIST_FILTER_PRESENT, CR_BUFFER_SMALL.
    internal const uint CmGetIdListFilterEnumerator = 0x1;
    internal const uint CmGetIdListFilterPresent = 0x100;
    internal const int CrBufferSmall = 0x1A;

    /// <summary>Size-then-list rounds before a list that keeps growing counts as unreadable.</summary>
    private const int DeviceListAttempts = 3;

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
    /// Whether any NVIDIA PCI device is present per the PnP device list — a
    /// disabled or driver-failed card is still listed there even when DXGI no
    /// longer enumerates it, and an unbound run must refuse loudly rather than
    /// silently retarget the iGPU in that state. Null when the list can't be
    /// read: a failed call, a size of zero (not even the list's terminator, on a
    /// machine whose Intel GPU is itself a PCI device), a list that kept growing
    /// past every retry, or cfgmgr32 missing. A null used to be false, which
    /// sent the run to the Intel adapter (R2-D).
    /// </summary>
    internal static bool? NvidiaPresentOnPciBus(DeviceIdListSize getSize, DeviceIdList getList)
    {
        try
        {
            // The documented pattern: a device arriving between the two calls
            // makes the list CR_BUFFER_SMALL, so size it again and retry.
            for (int attempt = 0; attempt < DeviceListAttempts; attempt++)
            {
                if (getSize(out uint length, "PCI", PciDeviceListFlags) != 0 || length == 0)
                {
                    return null;
                }

                var buffer = new char[length];
                int result = getList("PCI", buffer, length, PciDeviceListFlags);
                if (result == CrBufferSmall)
                {
                    continue;
                }

                return result == 0
                    ? new string(buffer).Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)
                    : null;
            }

            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
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
