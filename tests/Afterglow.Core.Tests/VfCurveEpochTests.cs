using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// A measured V/F plan never mixes measurements taken under different tuning
/// (R-20). Each bin carries the tuning it was measured under; a change starts
/// a new epoch; a plan is made only against the exact tuning its bins carry.
/// </summary>
[Collection("AppPaths")]
public sealed class VfCurveEpochTests : IDisposable
{
    private const string Uuid = "GPU-eeee1111-2222-3333-4444-555566667777";

    private static readonly VfTuningState Plus100 = new(100);

    private readonly StoreScope _store = new();

    public void Dispose() => _store.Dispose();

    private static GpuSnapshot Sample(double mv, uint mhz) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        DeviceIndex = 0,
        CoreVoltageMv = mv,
        CoreClockMHz = mhz,
        GpuUtilPct = 95,
    };

    private static void Measure(VfCurveRecorder recorder, uint clockAt900Mv, int count = 25)
    {
        for (int i = 0; i < count; i++)
        {
            recorder.Add(Sample(900, clockAt900Mv));
        }
    }

    [Fact]
    public void Applying_a_plan_and_planning_again_does_not_double_the_offset()
    {
        // The review's reproduction: 2800 MHz measured at 900 mV with offset 0,
        // plan 2900 -> +100; after applying it, the same target before any new
        // sample planned +200.
        var recorder = new VfCurveRecorder();
        recorder.SetTuningState(VfTuningState.Stock);
        Measure(recorder, 2800);

        var first = recorder.PlanUndervolt(900, 2900, VfTuningState.Stock);
        Assert.NotNull(first);
        Assert.Equal(100, first.CoreOffsetMHz);

        // The +100 is applied; the page hears of the write and says so.
        Assert.True(recorder.SetTuningState(Plus100), "the stock samples should have been set aside");
        Assert.Null(recorder.PlanUndervolt(900, 2900, Plus100));

        // Re-measured under +100 (the curve rose by the offset): the same target
        // is already held, so the plan is the offset in force — not +200.
        Measure(recorder, 2900);
        var again = recorder.PlanUndervolt(900, 2900, Plus100);
        Assert.NotNull(again);
        Assert.Equal(100, again.CoreOffsetMHz);
    }

    [Fact]
    public void A_missed_notification_still_cannot_plan_across_offsets()
    {
        // Nothing told the recorder about the +100; the plan-time check alone
        // must refuse to add +100 to a gap measured at stock.
        var recorder = new VfCurveRecorder();
        recorder.SetTuningState(VfTuningState.Stock);
        Measure(recorder, 2800);

        Assert.Null(recorder.PlanUndervolt(900, 2900, Plus100));
        Assert.Null(recorder.PlanUndervolt(900, 2900, currentOffsetMHz: 100));
        Assert.NotNull(recorder.PlanUndervolt(900, 2900, currentOffsetMHz: 0));
    }

    [Fact]
    public void Historical_boosted_samples_do_not_bias_a_plan_after_a_reset()
    {
        // 3000 MHz at 900 mV measured under +200; the card is reset to stock and
        // really runs 2800 there. Planning 2800 against the boosted max asked
        // for -200 MHz.
        var plus200 = new VfTuningState(200);
        var recorder = new VfCurveRecorder();
        recorder.SetTuningState(plus200);
        Measure(recorder, 3000);

        Assert.True(recorder.SetTuningState(VfTuningState.Stock));
        Assert.Empty(recorder.GetCurve());
        Assert.Null(recorder.PlanUndervolt(900, 2800, VfTuningState.Stock));

        Measure(recorder, 2800);
        var plan = recorder.PlanUndervolt(900, 2800, VfTuningState.Stock);
        Assert.NotNull(plan);
        Assert.Equal(0, plan.CoreOffsetMHz);
    }

    [Fact]
    public void Samples_of_unrecorded_tuning_are_drawn_but_never_planned_from()
    {
        var recorder = new VfCurveRecorder();
        Measure(recorder, 2800);

        Assert.Null(recorder.TuningState);
        Assert.NotEmpty(recorder.GetCurve());
        Assert.Null(recorder.PlanUndervolt(900, 2900, VfTuningState.Stock));

        // Declaring the tuning cannot vouch for samples taken before it was known.
        Assert.True(recorder.SetTuningState(VfTuningState.Stock));
        Assert.Empty(recorder.GetCurve());
    }

    [Fact]
    public void Declaring_the_same_tuning_again_keeps_the_curve()
    {
        // The probe's own pins and every unrelated write re-declare an
        // unchanged tuning; none of them may cost the curve.
        var recorder = new VfCurveRecorder();
        recorder.SetTuningState(Plus100);
        Measure(recorder, 2900);

        Assert.False(recorder.SetTuningState(new VfTuningState(100)));
        Assert.Equal(25L, recorder.TotalSamples);
        Assert.NotNull(recorder.PlanUndervolt(900, 2950, Plus100));
    }

    [Fact]
    public void The_voltage_sensor_verdict_survives_a_new_epoch()
    {
        // An epoch is about tuning; whether this card reports voltage is not.
        var recorder = new VfCurveRecorder();
        recorder.SetTuningState(VfTuningState.Stock);
        Measure(recorder, 2800);

        recorder.SetTuningState(Plus100);

        Assert.Equal(25L, recorder.SamplesWithVoltage);
        Assert.False(recorder.VoltageSensorLooksAbsent);
    }

    [Fact]
    public void A_shaped_table_is_never_planned_from()
    {
        // A plan is one global offset and erases per-point edits; the clock
        // measured at a shaped point contains that point's own edit.
        var shaped = new VfTuningState(0, "0123456789abcdef");
        var recorder = new VfCurveRecorder();
        recorder.SetTuningState(shaped);
        Measure(recorder, 2800);

        Assert.NotEmpty(recorder.GetCurve());
        Assert.Null(recorder.PlanUndervolt(900, 2900, shaped));
    }

    [Fact]
    public void The_epoch_is_saved_with_the_bins_and_restored_on_load()
    {
        var first = VfCurveRecorder.ForGpu(0, Uuid);
        first.SetTuningState(Plus100);
        Measure(first, 2900);
        Assert.True(first.Save());

        var next = VfCurveRecorder.ForGpu(0, Uuid);
        next.Load();

        Assert.Equal(Plus100, next.TuningState);
        Assert.NotNull(next.PlanUndervolt(900, 2950, Plus100));

        // The next session runs stock: the saved +100 curve is set aside, not merged.
        Assert.True(next.SetTuningState(VfTuningState.Stock));
        Assert.Null(next.PlanUndervolt(900, 2950, VfTuningState.Stock));
    }

    [Fact]
    public void A_curve_file_written_before_epochs_loads_as_unrecorded_tuning()
    {
        // The 1.5.0 shape: identity and bins, no tuning. Bin 180 = 900 mV.
        string path = VfCurveRecorder.PathFor(Uuid)!;
        AppPaths.EnsureCreated();
        File.WriteAllText(path,
            $$"""{"GpuUuid":"{{Uuid}}","VendorId":4318,"Bins":[{"Key":180,"MaxClock":2800,"ClockSum":70000,"Samples":25}]}""");

        var recorder = VfCurveRecorder.ForGpu(0, Uuid, 4318);
        recorder.Load();

        Assert.Null(recorder.TuningState);
        Assert.Single(recorder.GetCurve());
        Assert.Null(recorder.PlanUndervolt(900, 2900, VfTuningState.Stock));
    }

    [Fact]
    public void Loading_a_file_replaces_the_epoch_with_the_files()
    {
        var saved = VfCurveRecorder.ForGpu(0, Uuid);
        saved.SetTuningState(VfTuningState.Stock);
        Measure(saved, 2800);
        Assert.True(saved.Save());

        var recorder = VfCurveRecorder.ForGpu(0, Uuid);
        recorder.SetTuningState(Plus100);
        recorder.Load();

        // The bins came from a stock epoch; they must not wear the +100 label.
        Assert.Equal(VfTuningState.Stock, recorder.TuningState);
        Assert.Null(recorder.PlanUndervolt(900, 2900, Plus100));
    }

    [Fact]
    public void Adopting_a_tuning_keeps_known_provenance_data_but_never_relabels_an_epoch()
    {
        // The demo's synthetic stock curve: declared, not discarded.
        var demo = new VfCurveRecorder();
        Measure(demo, 2800);
        demo.AdoptTuningState(VfTuningState.Stock);
        Assert.NotNull(demo.PlanUndervolt(900, 2900, VfTuningState.Stock));

        // Once an epoch is known, adopting cannot rename it.
        demo.AdoptTuningState(Plus100);
        Assert.Equal(VfTuningState.Stock, demo.TuningState);
    }

    // --- Reading the tuning from a card ------------------------------------------

    private sealed class TableTuner(bool supportsVfPoints) : IGpuTuner
    {
        public int CoreOffsetMHz { get; set; }

        public IReadOnlyList<NvapiGpu.VfpTablePoint> Table { get; set; } = [];

        public NvapiStatus ReadStatus { get; set; } = NvapiStatus.Ok;

        public TuningCapabilities Capabilities { get; } = new() { SupportsVfPoints = supportsVfPoints };

        public string? GpuUuid => Uuid;

        public uint? AppliedLockMHz => null;

        public event Action<IGpuTuner>? StateWritten { add { } remove { } }

        public (int CoreOffsetMHz, int MemOffsetMHz, double? PowerLimitW, uint? VoltageBoostPct, uint? LockedCoreClockMHz)
            ReadCurrent() => (CoreOffsetMHz, 0, null, null, null);

        public NvapiStatus TryReadVfPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points)
        {
            points = ReadStatus == NvapiStatus.Ok ? Table : [];
            return ReadStatus;
        }

        public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true, bool releaseLock = false) =>
            throw new NotSupportedException();

        public ApplyResult ResetToDefaults() => throw new NotSupportedException();

        public KnobResult ForceUnlock() => throw new NotSupportedException();

        public NvmlReturn RestoreTuningLock(uint lockMHz) => throw new NotSupportedException();

        public NvmlReturn LockClockForProbe(uint clockMHz) => throw new NotSupportedException();

        public NvmlReturn SetAllFansRaw(uint dutyPct) => throw new NotSupportedException();

        public NvmlReturn SetFanRaw(uint coolerId, uint dutyPct) => throw new NotSupportedException();

        public NvmlReturn RestoreAutoFansRaw() => throw new NotSupportedException();

        public KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex) => throw new NotSupportedException();

        public KnobResult ClearVfPointOffsets() => throw new NotSupportedException();
    }

    private static List<NvapiGpu.VfpTablePoint> Table(params int[] offsets) =>
        [.. offsets.Select((offset, i) => new NvapiGpu.VfpTablePoint(i, 700 + (i * 25), 1300 + (i * 50), offset))];

    [Fact]
    public void A_card_without_a_point_table_is_described_by_its_core_offset()
    {
        var tuner = new TableTuner(supportsVfPoints: false) { CoreOffsetMHz = 75 };

        Assert.Equal(new VfTuningState(75), VfTuningState.Read(tuner));
    }

    [Fact]
    public void A_table_that_only_carries_the_global_offset_has_no_shape()
    {
        // Measured on the 5090: a global offset reads the same delta on every slot.
        var tuner = new TableTuner(supportsVfPoints: true) { CoreOffsetMHz = 100, Table = Table(100, 100, 100) };

        Assert.Equal(new VfTuningState(100), VfTuningState.Read(tuner));
    }

    [Fact]
    public void Per_point_edits_give_a_stable_shape_that_tells_edits_apart()
    {
        var tuner = new TableTuner(supportsVfPoints: true) { Table = Table(0, -50, -100) };
        var shaped = VfTuningState.Read(tuner);

        Assert.NotNull(shaped.PointShape);
        Assert.Equal(shaped, VfTuningState.Read(tuner)); // same edits, same state: no spurious epoch

        tuner.Table = Table(0, -50, -150);
        Assert.NotEqual(shaped, VfTuningState.Read(tuner));

        // Base clocks can move with temperature; only the offsets are tuning.
        tuner.Table = [.. Table(0, -50, -100).Select(p => p with { ClockMHz = p.ClockMHz - 15 })];
        Assert.Equal(shaped, VfTuningState.Read(tuner));
    }

    [Fact]
    public void A_uniform_table_that_disagrees_with_the_core_offset_counts_as_shaped()
    {
        // The clear verdict's odd case: every point reads +50 while the driver
        // reports +100. The table is what the card runs; it is not "+100, no shape".
        var tuner = new TableTuner(supportsVfPoints: true) { CoreOffsetMHz = 100, Table = Table(50, 50, 50) };

        Assert.NotNull(VfTuningState.Read(tuner).PointShape);
    }

    [Fact]
    public void A_table_that_will_not_read_cannot_rule_out_edits()
    {
        var tuner = new TableTuner(supportsVfPoints: true) { ReadStatus = NvapiStatus.Error };

        Assert.Equal(VfTuningState.UnreadableShape, VfTuningState.Read(tuner).PointShape);
    }
}
