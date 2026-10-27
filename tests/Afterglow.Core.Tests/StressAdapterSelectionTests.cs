using Afterglow.Core.Stress;
using Candidate = Afterglow.Core.Stress.StressAdapter.AdapterCandidate;

namespace Afterglow.Core.Tests;

/// <summary>
/// The burn-adapter selection policy, without DXGI: which enumerated NVIDIA
/// adapter a stress/VRAM/stepper/certify run loads for the card being tuned.
/// Evidence gathered on the wrong card certifies an overclock nobody tested,
/// so a known mismatch must be refused, never relabelled (F-17, F-62).
/// </summary>
public class StressAdapterSelectionTests
{
    [Fact]
    public void Requested_bus_picks_the_adapter_on_that_bus()
    {
        var candidates = new[]
        {
            new Candidate("RTX 5090", 32L << 30, Bus: 1),
            new Candidate("RTX 4070", 12L << 30, Bus: 13),
        };

        int pick = StressAdapter.Choose(candidates, pciBusId: 13, out string description);

        Assert.Equal(1, pick);
        Assert.Contains("PCI bus 13", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lone_adapter_whose_bus_query_failed_is_used_and_labelled_unverified()
    {
        // The case the fallback exists for: one NVIDIA adapter, D3DKMT could not
        // say which bus it is on, so there is nothing to contradict the request.
        var candidates = new[] { new Candidate("RTX 5090", 32L << 30, Bus: null) };

        int pick = StressAdapter.Choose(candidates, pciBusId: 1, out string description);

        Assert.Equal(0, pick);
        Assert.Contains("bus unverified", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lone_adapter_known_to_be_on_another_bus_is_refused_and_says_so()
    {
        // NVML sees a second card (TCC/compute mode, hidden from DXGI) on bus 13;
        // DXGI's only NVIDIA adapter answered the bus query: bus 1. Loading it
        // would burn the display card and certify the tuned one untested.
        var candidates = new[] { new Candidate("RTX 5090", 32L << 30, Bus: 1) };

        int pick = StressAdapter.Choose(candidates, pciBusId: 13, out string description);

        Assert.Equal(-1, pick);
        Assert.Contains("PCI bus 1", description, StringComparison.Ordinal);
        Assert.Contains("13", description, StringComparison.Ordinal);

        // The query did not fail — it answered, and the answer was "not this card".
        Assert.DoesNotContain("unavailable", description, StringComparison.Ordinal);
        Assert.DoesNotContain("unverified", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_adapters_none_on_the_requested_bus_are_refused()
    {
        var candidates = new[]
        {
            new Candidate("RTX 5090", 32L << 30, Bus: 1),
            new Candidate("RTX 4070", 12L << 30, Bus: null),
        };

        int pick = StressAdapter.Choose(candidates, pciBusId: 13, out string description);

        Assert.Equal(-1, pick);
        Assert.Contains("refusing", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_bus_the_largest_vram_adapter_is_chosen()
    {
        var candidates = new[]
        {
            new Candidate("RTX 4070", 12L << 30, Bus: 13),
            new Candidate("RTX 5090", 32L << 30, Bus: 1),
        };

        int pick = StressAdapter.Choose(candidates, pciBusId: null, out string description);

        Assert.Equal(1, pick);
        Assert.StartsWith("RTX 5090", description, StringComparison.Ordinal);

        // With two candidates and no bus the pick is a guess, and says so, so a
        // caller that must not guess (certification) can refuse it.
        Assert.True(StressAdapter.IsUnboundGuess(description));
    }

    [Fact]
    public void A_single_adapter_without_a_bus_is_not_a_guess()
    {
        var candidates = new[] { new Candidate("RTX 5090", 32L << 30, Bus: 1) };

        int pick = StressAdapter.Choose(candidates, pciBusId: null, out string description);

        Assert.Equal(0, pick);
        Assert.Equal("RTX 5090", description);
        Assert.False(StressAdapter.IsUnboundGuess(description));
    }

    [Fact]
    public void No_nvidia_adapter_means_no_pick()
    {
        Assert.Equal(-1, StressAdapter.Choose([], pciBusId: 1, out _));
        Assert.Equal(-1, StressAdapter.Choose([], pciBusId: null, out _));
    }

    // --- Default vendor for an unbound run (no card named) --------------------

    private const string NvidiaInstance = @"PCI\VEN_10DE&DEV_2684&SUBSYS_16F110DE&REV_A1\4&2B8F3C1A&0&0008";
    private const string IntelInstance = @"PCI\VEN_8086&DEV_E20B&SUBSYS_00008086&REV_00\3&11583659&0&10";
    private const uint MicrosoftVendorId = 0x1414;   // WARP / Microsoft Basic Render Driver
    private const uint AmdVendorId = 0x1002;

    private static readonly StressAdapter.AdapterVendor IntelGpu = new(StressAdapter.IntelVendorId, Software: false);
    private static readonly StressAdapter.AdapterVendor BasicRender = new(MicrosoftVendorId, Software: true);

    // cfgmgr32.h CONFIGRET values.
    private const int CrFailure = 0x13;
    private const int CrInvalidPointer = 0x3;

    /// <summary>
    /// A stand-in PnP device tree for the two cfgmgr32 calls: each device
    /// instance is present or only remembered (a "phantom" — a card that was
    /// removed, or an eGPU that is unplugged), and both calls honour
    /// CM_GETIDLIST_FILTER_PRESENT the way the documented API does. It can also
    /// fail the way the real calls can: an error code, a zero size, a missing
    /// DLL or export, or devices arriving between the size and the list.
    /// </summary>
    private sealed class FakeDeviceTree(params (string Id, bool Present)[] devices)
    {
        private readonly List<(string Id, bool Present)> _devices = [.. devices];

        public List<(string Call, string Filter, uint Flags)> Calls { get; } = [];

        /// <summary>CONFIGRET of every size call (0 = CR_SUCCESS).</summary>
        public int SizeResult { get; init; }

        /// <summary>CONFIGRET of every list call whose buffer is big enough.</summary>
        public int ListResult { get; init; }

        /// <summary>Report a length of zero.</summary>
        public bool ZeroLength { get; init; }

        /// <summary>Thrown by both calls, as the marshaller does when cfgmgr32 or its export can't be loaded.</summary>
        public Exception? Throws { get; init; }

        /// <summary>One device arrives right after each size call while any are queued, so that list no longer fits.</summary>
        public Queue<string> Arrivals { get; } = new();

        private string MultiSz(uint flags)
        {
            bool presentOnly = (flags & StressAdapter.CmGetIdListFilterPresent) != 0;
            var ids = _devices.Where(d => d.Present || !presentOnly).Select(d => d.Id + "\0");
            return string.Concat(ids) + "\0";
        }

        public int Size(out uint length, string filter, uint flags)
        {
            Calls.Add(("size", filter, flags));
            if (Throws is { } ex)
            {
                throw ex;
            }

            length = ZeroLength ? 0 : (uint)MultiSz(flags).Length;
            if (Arrivals.TryDequeue(out string? arrived))
            {
                _devices.Add((arrived, true));
            }

            return SizeResult;
        }

        public int List(string filter, char[] buffer, uint bufferLength, uint flags)
        {
            Calls.Add(("list", filter, flags));
            string list = MultiSz(flags);
            if (list.Length > bufferLength)
            {
                return StressAdapter.CrBufferSmall;
            }

            if (ListResult != 0)
            {
                return ListResult;
            }

            list.CopyTo(0, buffer, 0, list.Length);
            return 0;
        }

        public bool? NvidiaPresent() => StressAdapter.NvidiaPresentOnPciBus(Size, List);
    }

    [Fact]
    public void A_remembered_but_removed_nvidia_card_does_not_block_the_intel_adapter()
    {
        // R-42: the old NVIDIA card was swapped for an Intel Arc (or an NVIDIA
        // eGPU is unplugged). PnP still remembers its device node; counted as
        // installed hardware, it made an unbound stress/vram run pick NVIDIA
        // and refuse the working Intel adapter.
        var tree = new FakeDeviceTree((NvidiaInstance, Present: false), (IntelInstance, Present: true));

        var choice = StressAdapter.ChooseDefaultVendor([IntelGpu, BasicRender], tree.NvidiaPresent);

        Assert.Equal(StressAdapter.IntelVendorId, choice.VendorId);
        Assert.Null(choice.Refusal);
    }

    [Theory]
    [InlineData("size call fails")]
    [InlineData("list call fails")]
    [InlineData("size is zero")]
    [InlineData("cfgmgr32 missing")]
    [InlineData("export missing")]
    [InlineData("list keeps growing")]
    public void A_device_list_that_cannot_be_read_refuses_instead_of_testing_intel(string failure)
    {
        // R2-D: DXGI shows only Intel, and Windows' PCI device list — the one
        // thing that can reveal a disabled or TDR-dropped NVIDIA card — can't be
        // read. Reading "couldn't ask" as "no NVIDIA card" sent an unbound run
        // to the Intel adapter, the opposite of the documented fallback. Before
        // the fix every row here picked Intel.
        var tree = failure switch
        {
            "size call fails" => new FakeDeviceTree((IntelInstance, true)) { SizeResult = CrFailure },
            "list call fails" => new FakeDeviceTree((IntelInstance, true)) { ListResult = CrInvalidPointer },
            "size is zero" => new FakeDeviceTree((IntelInstance, true)) { ZeroLength = true },
            "cfgmgr32 missing" => new FakeDeviceTree((IntelInstance, true)) { Throws = new DllNotFoundException("cfgmgr32.dll") },
            "export missing" => new FakeDeviceTree((IntelInstance, true)) { Throws = new EntryPointNotFoundException("CM_Get_Device_ID_ListW") },
            _ => new FakeDeviceTree((IntelInstance, true)),
        };
        if (failure == "list keeps growing")
        {
            for (int i = 0; i < 10; i++)
            {
                tree.Arrivals.Enqueue($@"PCI\VEN_8086&DEV_{i:X4}\HOTPLUG&{i}");
            }
        }

        Assert.Null(tree.NvidiaPresent());

        var choice = StressAdapter.ChooseDefaultVendor([IntelGpu, BasicRender], tree.NvidiaPresent);

        // Fails closed: a reason to show, and NVIDIA for any caller that
        // ignores it, so the engines find no NVIDIA adapter and refuse too.
        Assert.Equal(StressAdapter.DeviceListUnreadableReason, choice.Refusal);
        Assert.Equal(StressAdapter.NvidiaVendorId, choice.VendorId);
        Assert.NotEqual(StressAdapter.IntelVendorId, choice.VendorId);
    }

    [Fact]
    public void A_device_arriving_between_the_two_calls_is_asked_again_not_refused()
    {
        // The documented pattern for CM_Get_Device_ID_List: CR_BUFFER_SMALL
        // means the list grew after it was sized, so size it and ask again.
        // Now that an unreadable list refuses the run, a hot-plug race must not.
        var tree = new FakeDeviceTree((IntelInstance, Present: true));
        tree.Arrivals.Enqueue(@"PCI\VEN_8086&DEV_A0C8&SUBSYS_00008086&REV_00\3&11583659&0&FB");

        var choice = StressAdapter.ChooseDefaultVendor([IntelGpu, BasicRender], tree.NvidiaPresent);

        Assert.Equal(StressAdapter.IntelVendorId, choice.VendorId);
        Assert.Null(choice.Refusal);
        Assert.Equal(["size", "list", "size", "list"], tree.Calls.Select(c => c.Call));
    }

    [Fact]
    public void A_refused_unbound_cli_run_says_how_to_name_the_card()
    {
        // `stress` and `vram` print this and exit 1 before loading anything.
        string message = Afterglow.Cli.CliGpu.UnboundRefusal(StressAdapter.DeviceListUnreadableReason);

        Assert.StartsWith(StressAdapter.DeviceListUnreadableReason, message, StringComparison.Ordinal);
        Assert.Contains("--gpu N", message, StringComparison.Ordinal);
        Assert.Contains("stress --probe-adapter", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_device_list_calls_ask_for_present_pci_devices_only()
    {
        // The size and the list must be asked the same question: a size for
        // present devices with a list of all remembered ones (or the reverse)
        // either overflows the buffer or counts phantoms (R-42).
        var tree = new FakeDeviceTree((IntelInstance, Present: true));

        Assert.False(tree.NvidiaPresent());

        Assert.Equal(["size", "list"], tree.Calls.Select(c => c.Call));
        Assert.All(tree.Calls, c =>
        {
            Assert.Equal("PCI", c.Filter);
            Assert.NotEqual(0u, c.Flags & StressAdapter.CmGetIdListFilterEnumerator);
            Assert.NotEqual(0u, c.Flags & StressAdapter.CmGetIdListFilterPresent);
        });
    }

    [Fact]
    public void A_present_nvidia_card_hidden_from_dxgi_still_forces_the_refusal()
    {
        // A disabled or driver-failed (TDR-dropped) NVIDIA card is absent from
        // DXGI but its device node is still present: the run must refuse loudly
        // rather than silently burn the iGPU instead.
        var tree = new FakeDeviceTree((NvidiaInstance, Present: true), (IntelInstance, Present: true));

        var choice = StressAdapter.ChooseDefaultVendor([IntelGpu, BasicRender], tree.NvidiaPresent);

        // Refused before anything loads, with a reason that names the cause
        // (the engines' "No NVIDIA adapter found" never said there IS one).
        Assert.Equal(StressAdapter.NvidiaVendorId, choice.VendorId);
        Assert.Equal(StressAdapter.NvidiaHiddenFromDirectXReason, choice.Refusal);
    }

    [Fact]
    public void An_nvidia_adapter_in_dxgi_wins_without_asking_the_pci_bus()
    {
        bool asked = false;

        var choice = StressAdapter.ChooseDefaultVendor(
            [IntelGpu, new(StressAdapter.NvidiaVendorId, Software: false)],
            () => asked = true);

        Assert.Equal(StressAdapter.NvidiaVendorId, choice.VendorId);
        Assert.Null(choice.Refusal);
        Assert.False(asked);
    }

    [Fact]
    public void Intel_beside_another_hardware_vendor_keeps_the_historical_refusal()
    {
        var tree = new FakeDeviceTree((IntelInstance, Present: true));

        var choice = StressAdapter.ChooseDefaultVendor(
            [IntelGpu, new(AmdVendorId, Software: false)], tree.NvidiaPresent);

        // NVIDIA without a reason: the engines refuse ("No NVIDIA adapter found").
        Assert.Equal(StressAdapter.NvidiaVendorId, choice.VendorId);
        Assert.Null(choice.Refusal);
        Assert.Empty(tree.Calls);
    }

    [Fact]
    public void Software_adapters_alone_are_not_an_intel_machine()
    {
        var tree = new FakeDeviceTree();

        Assert.Equal(StressAdapter.DefaultVendorChoice.Vendor(StressAdapter.NvidiaVendorId),
            StressAdapter.ChooseDefaultVendor([BasicRender], tree.NvidiaPresent));
        Assert.Equal(StressAdapter.DefaultVendorChoice.Vendor(StressAdapter.NvidiaVendorId),
            StressAdapter.ChooseDefaultVendor([], tree.NvidiaPresent));
    }
}
