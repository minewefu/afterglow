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

    /// <summary>
    /// A stand-in PnP device tree for the two cfgmgr32 calls: each device
    /// instance is present or only remembered (a "phantom" — a card that was
    /// removed, or an eGPU that is unplugged), and both calls honour
    /// CM_GETIDLIST_FILTER_PRESENT the way the documented API does.
    /// </summary>
    private sealed class FakeDeviceTree(params (string Id, bool Present)[] devices)
    {
        public List<(string Call, string Filter, uint Flags)> Calls { get; } = [];

        private string MultiSz(uint flags)
        {
            bool presentOnly = (flags & StressAdapter.CmGetIdListFilterPresent) != 0;
            var ids = devices.Where(d => d.Present || !presentOnly).Select(d => d.Id + "\0");
            return string.Concat(ids) + "\0";
        }

        public int Size(out uint length, string filter, uint flags)
        {
            Calls.Add(("size", filter, flags));
            length = (uint)MultiSz(flags).Length;
            return 0;
        }

        public int List(string filter, char[] buffer, uint bufferLength, uint flags)
        {
            Calls.Add(("list", filter, flags));
            string list = MultiSz(flags);
            if (list.Length > bufferLength)
            {
                return 0x1A;   // CR_BUFFER_SMALL
            }

            list.CopyTo(0, buffer, 0, list.Length);
            return 0;
        }

        public bool NvidiaPresent() => StressAdapter.NvidiaPresentOnPciBus(Size, List);
    }

    [Fact]
    public void A_remembered_but_removed_nvidia_card_does_not_block_the_intel_adapter()
    {
        // R-42: the old NVIDIA card was swapped for an Intel Arc (or an NVIDIA
        // eGPU is unplugged). PnP still remembers its device node; counted as
        // installed hardware, it made an unbound stress/vram run pick NVIDIA
        // and refuse the working Intel adapter.
        var tree = new FakeDeviceTree((NvidiaInstance, Present: false), (IntelInstance, Present: true));

        uint vendor = StressAdapter.ChooseDefaultVendor([IntelGpu, BasicRender], tree.NvidiaPresent);

        Assert.Equal(StressAdapter.IntelVendorId, vendor);
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

        uint vendor = StressAdapter.ChooseDefaultVendor([IntelGpu, BasicRender], tree.NvidiaPresent);

        Assert.Equal(StressAdapter.NvidiaVendorId, vendor);
    }

    [Fact]
    public void An_nvidia_adapter_in_dxgi_wins_without_asking_the_pci_bus()
    {
        bool asked = false;

        uint vendor = StressAdapter.ChooseDefaultVendor(
            [IntelGpu, new(StressAdapter.NvidiaVendorId, Software: false)],
            () => asked = true);

        Assert.Equal(StressAdapter.NvidiaVendorId, vendor);
        Assert.False(asked);
    }

    [Fact]
    public void Intel_beside_another_hardware_vendor_keeps_the_historical_refusal()
    {
        var tree = new FakeDeviceTree((IntelInstance, Present: true));

        uint vendor = StressAdapter.ChooseDefaultVendor(
            [IntelGpu, new(AmdVendorId, Software: false)], tree.NvidiaPresent);

        Assert.Equal(StressAdapter.NvidiaVendorId, vendor);
    }

    [Fact]
    public void Software_adapters_alone_are_not_an_intel_machine()
    {
        var tree = new FakeDeviceTree();

        Assert.Equal(StressAdapter.NvidiaVendorId, StressAdapter.ChooseDefaultVendor([BasicRender], tree.NvidiaPresent));
        Assert.Equal(StressAdapter.NvidiaVendorId, StressAdapter.ChooseDefaultVendor([], tree.NvidiaPresent));
    }
}
