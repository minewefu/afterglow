using System.Text.Json.Nodes;
using Afterglow.Cli;
using Afterglow.Core.Hardware;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// MCP's save_profile must capture what the GUI's save captures (R-35). It
/// read only ReadCurrent's scalars, so a shaped curve saved through MCP came
/// back without its points and with <see cref="TuningProfile.CapturedVfPoints"/>
/// false: reset, apply the profile, and the curve the agent had saved was gone.
/// These drive the tool's own body against a fake card; nothing opens a GPU.
/// </summary>
public class McpSaveProfileTests : IDisposable
{
    private const int CoreOffset = 150;
    private const string Uuid = "GPU-11111111-2222-3333-4444-555555555555";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "afterglow-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>A card with a readable per-point table; counts every table read.</summary>
    private sealed class TableTuner(bool supportsVfPoints, NvapiStatus readStatus, IReadOnlyList<NvapiGpu.VfpTablePoint> table)
        : IGpuTuner
    {
        public int TableReads { get; private set; }

        public TuningCapabilities Capabilities { get; } = new()
        {
            SupportsCoreOffset = true,
            SupportsVfPoints = supportsVfPoints,
        };

        public string? GpuUuid => Uuid;

        public uint? AppliedLockMHz => null;

        public event Action<IGpuTuner>? StateWritten { add { } remove { } }

        public (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz)
            ReadCurrent() => (CoreOffset, 500, 320, null, null);

        public NvapiStatus TryReadVfPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points)
        {
            TableReads++;
            points = readStatus == NvapiStatus.Ok ? table : [];
            return readStatus;
        }

        public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true, bool releaseLock = false) =>
            throw new InvalidOperationException("save_profile must not write");

        public ApplyResult ResetToDefaults() => throw new InvalidOperationException("save_profile must not write");

        public KnobResult ForceUnlock() => throw new InvalidOperationException("save_profile must not write");

        public NvmlReturn RestoreTuningLock(uint lockMHz) => NvmlReturn.NotSupported;

        public NvmlReturn LockClockForProbe(uint clockMHz) => NvmlReturn.NotSupported;

        public NvmlReturn SetAllFansRaw(uint dutyPct) => NvmlReturn.NotSupported;

        public NvmlReturn SetFanRaw(uint coolerId, uint dutyPct) => NvmlReturn.NotSupported;

        public NvmlReturn RestoreAutoFansRaw() => NvmlReturn.NotSupported;

        public KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex) =>
            throw new InvalidOperationException("save_profile must not write");

        public KnobResult ClearVfPointOffsets() => throw new InvalidOperationException("save_profile must not write");
    }

    private sealed class NoSensors : ISensorSource
    {
        public uint DeviceIndex => 1;

        public GpuSnapshot Poll() => new() { Timestamp = DateTimeOffset.Now, DeviceIndex = 1 };
    }

    private static GpuContext Card(IGpuTuner tuner) => new()
    {
        Index = 1,
        Name = "Fake RTX 5090",
        Vendor = GpuVendor.Nvidia,
        Poller = new NoSensors(),
        Tuner = tuner,
        Uuid = Uuid,
    };

    /// <summary>
    /// A flatten written over a +150 core offset, on raw slots 40, 42, 44 ... —
    /// deliberately not 0..n, so a capture keyed by list position instead of
    /// table slot would put every offset on the wrong point.
    /// </summary>
    private static List<NvapiGpu.VfpTablePoint> ShapedTable()
    {
        var points = new List<NvapiGpu.VfpTablePoint>();
        int slot = 40;
        for (double mv = 700; mv <= 1050; mv += 25, slot += 2)
        {
            double stock = Math.Round(1300 + ((mv - 700) / 350.0 * 800));
            int offset = mv < 875 ? 0 : (int)Math.Round(1875 - stock);
            points.Add(new NvapiGpu.VfpTablePoint(slot, mv, stock, offset));
        }

        return points;
    }

    private (TuningProfile Saved, TableTuner Tuner) Save(bool supportsVfPoints, NvapiStatus readStatus, List<NvapiGpu.VfpTablePoint> table)
    {
        var tuner = new TableTuner(supportsVfPoints, readStatus, table);
        var store = new ProfileStore(_dir);
        McpCommand.SaveProfile(Card(tuner), store, new JsonObject { ["name"] = "agent-curve" });

        var saved = store.Load("agent-curve");
        Assert.NotNull(saved);
        return (saved!, tuner);
    }

    [Fact]
    public void A_shaped_curve_is_saved_whole_keyed_by_table_slot()
    {
        var table = ShapedTable();
        var (saved, tuner) = Save(supportsVfPoints: true, NvapiStatus.Ok, table);

        // The review's harness saw tableReads=0, CapturedVfPoints=false, offsets=null.
        Assert.Equal(1, tuner.TableReads);
        Assert.True(saved.CapturedVfPoints);
        Assert.NotNull(saved.VfPointOffsetsMHz);

        // Whole table, explicit zeros included (F-02/F-07), on the raw slots.
        Assert.Equal(table.ToDictionary(p => p.Index, p => p.OffsetMHz), saved.VfPointOffsetsMHz);
        Assert.Contains(saved.VfPointOffsetsMHz!, pair => pair.Value == 0);

        // Identity: the card the table was read from, as the GUI stamps it.
        Assert.Equal(Uuid, saved.GpuUuid);
        Assert.Equal("Fake RTX 5090", saved.GpuName);
        Assert.Equal(CoreOffset, saved.CoreOffsetMHz);
        Assert.Null(saved.Validate());
    }

    [Fact]
    public void A_table_holding_only_the_core_offset_is_recorded_as_read_with_no_points()
    {
        // Read, and nothing beyond the core offset: "no per-point offsets" is
        // now a fact about this card, so applying the profile may remove a curve.
        var uniform = ShapedTable().Select(p => p with { OffsetMHz = CoreOffset }).ToList();
        var (saved, tuner) = Save(supportsVfPoints: true, NvapiStatus.Ok, uniform);

        Assert.Equal(1, tuner.TableReads);
        Assert.True(saved.CapturedVfPoints);
        Assert.Null(saved.VfPointOffsetsMHz);
    }

    [Fact]
    public void A_table_that_did_not_read_leaves_the_profile_with_no_opinion_on_the_curve()
    {
        // Never "captured, empty": that would let applying the profile delete a
        // curve nobody saw.
        var (saved, tuner) = Save(supportsVfPoints: true, NvapiStatus.Error, ShapedTable());

        Assert.Equal(1, tuner.TableReads);
        Assert.False(saved.CapturedVfPoints);
        Assert.Null(saved.VfPointOffsetsMHz);
    }

    [Fact]
    public void A_card_without_per_point_control_is_never_asked_for_its_table()
    {
        var (saved, tuner) = Save(supportsVfPoints: false, NvapiStatus.Ok, ShapedTable());

        Assert.Equal(0, tuner.TableReads);
        Assert.False(saved.CapturedVfPoints);
        Assert.Null(saved.VfPointOffsetsMHz);
        Assert.Equal(Uuid, saved.GpuUuid);
    }

    [Fact]
    public void The_shared_capture_with_no_card_selected_stamps_nothing_and_reads_nothing()
    {
        // The Profiles page's case: no GPU selected. It used to build exactly
        // this inline; it now calls the same ProfileCapture as MCP.
        var profile = new TuningProfile { Name = "no card" }.CapturedFrom(null);

        Assert.Null(profile.GpuUuid);
        Assert.Null(profile.GpuName);
        Assert.Null(profile.VfPointOffsetsMHz);
        Assert.False(profile.CapturedVfPoints);
    }

    [Fact]
    public void The_shared_capture_never_keeps_a_stale_table_the_card_did_not_answer_for()
    {
        // Whatever the caller's profile already carried, the result describes
        // this card's read and nothing else — a stale "captured" pair surviving
        // a failed read would let apply delete a curve nobody saw.
        var stale = new TuningProfile
        {
            Name = "stale",
            GpuUuid = "GPU-other",
            VfPointOffsetsMHz = new Dictionary<int, int> { [40] = -200 },
            CapturedVfPoints = true,
        };
        var tuner = new TableTuner(supportsVfPoints: true, NvapiStatus.Error, ShapedTable());

        var profile = stale.CapturedFrom(Card(tuner));

        Assert.Equal(Uuid, profile.GpuUuid);
        Assert.Null(profile.VfPointOffsetsMHz);
        Assert.False(profile.CapturedVfPoints);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
