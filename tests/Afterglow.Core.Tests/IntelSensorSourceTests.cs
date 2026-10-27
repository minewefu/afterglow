using Afterglow.Core.Interop.Igcl;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tests.Fakes;

namespace Afterglow.Core.Tests;

/// <summary>
/// The counter-delta math that turns IGCL's monotonic energy/activity counters
/// into watts and utilization — pure logic, no driver. The honest-null rules
/// matter as much as the arithmetic: unsupported samples, unexpected units,
/// zero/negative time, and counter resets must all produce null, never a
/// plausible-looking number.
/// </summary>
public class IntelSensorSourceTests
{
    private static CtlTelemetryItem Joules(double value) => Item(value, CtlUnits.EnergyJoules);

    private static CtlTelemetryItem Seconds(double value) => Item(value, CtlUnits.TimeSeconds);

    private static CtlTelemetryItem Item(double value, CtlUnits units)
    {
        var item = new CtlTelemetryItem { Supported = 1, Units = units, Type = CtlDataType.Double };
        item.Value.DataDouble = value;
        return item;
    }

    [Fact]
    public void Watts_are_energy_delta_over_time_delta()
    {
        // 15 J over 1.5 s = 10 W.
        Assert.Equal(10.0, IntelSensorSource.PowerFromEnergyCounters(Joules(100), Joules(115), 1.5));
    }

    [Fact]
    public void Power_is_null_when_either_sample_is_unsupported()
    {
        var unsupported = default(CtlTelemetryItem);
        Assert.Null(IntelSensorSource.PowerFromEnergyCounters(unsupported, Joules(115), 1.0));
        Assert.Null(IntelSensorSource.PowerFromEnergyCounters(Joules(100), unsupported, 1.0));
    }

    [Fact]
    public void Power_is_null_for_non_joule_units_rather_than_a_wrong_number()
    {
        // A driver reporting the counter in some other encoding must not be
        // divided as if it were joules.
        Assert.Null(IntelSensorSource.PowerFromEnergyCounters(
            Item(100, CtlUnits.PowerMilliwatts), Item(115, CtlUnits.PowerMilliwatts), 1.0));
    }

    [Fact]
    public void Power_is_null_on_zero_or_negative_time_and_on_counter_reset()
    {
        Assert.Null(IntelSensorSource.PowerFromEnergyCounters(Joules(100), Joules(115), 0));
        Assert.Null(IntelSensorSource.PowerFromEnergyCounters(Joules(100), Joules(115), -0.5));
        // Counter went backwards (driver reset): no negative watts.
        Assert.Null(IntelSensorSource.PowerFromEnergyCounters(Joules(115), Joules(100), 1.0));
    }

    [Fact]
    public void Utilization_is_busy_delta_over_time_delta_as_percent()
    {
        // 0.5 busy-seconds over 1.0 s = 50%.
        Assert.Equal(50u, IntelSensorSource.UtilFromActivityCounters(Seconds(10.0), Seconds(10.5), 1.0));
    }

    [Fact]
    public void Utilization_clamps_to_100_when_counters_outrun_the_clock()
    {
        // Busy time exceeding wall time (timestamp jitter) clamps instead of
        // reporting an impossible 120%.
        Assert.Equal(100u, IntelSensorSource.UtilFromActivityCounters(Seconds(10.0), Seconds(11.2), 1.0));
    }

    [Fact]
    public void Utilization_is_null_for_unsupported_samples_wrong_units_or_reset()
    {
        var unsupported = default(CtlTelemetryItem);
        Assert.Null(IntelSensorSource.UtilFromActivityCounters(unsupported, Seconds(10.5), 1.0));
        Assert.Null(IntelSensorSource.UtilFromActivityCounters(Seconds(10.0), Seconds(10.5), 0));
        Assert.Null(IntelSensorSource.UtilFromActivityCounters(
            Item(10.0, CtlUnits.Percent), Item(10.5, CtlUnits.Percent), 1.0));
        Assert.Null(IntelSensorSource.UtilFromActivityCounters(Seconds(10.5), Seconds(10.0), 1.0));
    }

    [Fact]
    public void Integer_typed_counters_decode_through_the_union()
    {
        var before = new CtlTelemetryItem { Supported = 1, Units = CtlUnits.EnergyJoules, Type = CtlDataType.Uint64 };
        before.Value.DataU64 = 1000;
        var after = new CtlTelemetryItem { Supported = 1, Units = CtlUnits.EnergyJoules, Type = CtlDataType.Uint64 };
        after.Value.DataU64 = 1020;
        Assert.Equal(20.0, IntelSensorSource.PowerFromEnergyCounters(before, after, 1.0));
    }
}

/// <summary>
/// The real <see cref="IntelSensorSource.Poll"/> against an in-memory IGCL
/// adapter: which counter board power comes from (R-18) and where the memory
/// temperature comes from when no memory sensor is enumerated (R-19). Samples
/// are one second apart, so a counter's per-sample step is its watts.
/// </summary>
public class IntelSensorSourcePollTests
{
    private static CtlTelemetryItem Joules(double value) => FakeIntelTelemetryDevice.Item(value, CtlUnits.EnergyJoules);

    private static CtlTelemetryItem Celsius(double value) => FakeIntelTelemetryDevice.Item(value, CtlUnits.TemperatureCelsius);

    /// <summary>
    /// One bulk block at <paramref name="second"/>: the GPU-chip counter steps
    /// 50 J/s and, when present, the total-card counter 100 J/s — the
    /// review's discrete-board shape, where the card draws twice the chip.
    /// </summary>
    private static CtlPowerTelemetry Sample(int second, bool card = true, CtlUnits cardUnits = CtlUnits.EnergyJoules, double? vramC = null)
    {
        var t = new CtlPowerTelemetry
        {
            TimeStamp = FakeIntelTelemetryDevice.Item(second, CtlUnits.TimeSeconds),
            GpuEnergyCounter = Joules(3600 + (50 * second)),
        };
        if (card)
        {
            t.TotalCardEnergyCounter = FakeIntelTelemetryDevice.Item(7200 + (100 * second), cardUnits);
        }

        if (vramC is double c)
        {
            t.VramCurrentTemperature = Celsius(c);
        }

        return t;
    }

    private static GpuSnapshot SecondPoll(FakeIntelTelemetryDevice device)
    {
        var source = new IntelSensorSource(device, 0);
        _ = source.Poll();
        return source.Poll();
    }

    [Fact]
    public void Board_power_comes_from_the_total_card_counter_when_the_driver_supports_it()
    {
        // R-18: chip +50 J and card +100 J over one second. The card drew
        // 100 W; reporting the chip's 50 W as board power halved it.
        var snapshot = SecondPoll(new FakeIntelTelemetryDevice(Sample(0), Sample(1)));

        Assert.Equal(100.0, snapshot.PowerW);
        Assert.Equal(100.0, snapshot.PowerAvgW);
        // 7300 J of card energy = 7300 / 3600 Wh.
        Assert.Equal(7300 / 3600.0, snapshot.EnergyWh);
        Assert.False(snapshot.PowerIsChipOnly);
    }

    [Fact]
    public void Without_a_card_counter_the_chip_counter_still_answers()
    {
        // The fallback this source always used — kept, not dropped to "—".
        var snapshot = SecondPoll(new FakeIntelTelemetryDevice(Sample(0, card: false), Sample(1, card: false)));

        Assert.Equal(50.0, snapshot.PowerW);
        Assert.Equal(50.0, snapshot.PowerAvgW);
        Assert.Equal(3650 / 3600.0, snapshot.EnergyWh);
        // ...and says it measured the chip, not the board.
        Assert.True(snapshot.PowerIsChipOnly);
    }

    [Fact]
    public void A_card_counter_in_non_joule_units_falls_back_to_the_chip()
    {
        // The card item is "supported" but not in joules: dividing it as
        // joules would be a guess, so the chip counter answers instead.
        var snapshot = SecondPoll(new FakeIntelTelemetryDevice(
            Sample(0, cardUnits: CtlUnits.PowerMilliwatts), Sample(1, cardUnits: CtlUnits.PowerMilliwatts)));

        Assert.Equal(50.0, snapshot.PowerW);
        Assert.Equal(3650 / 3600.0, snapshot.EnergyWh);
        Assert.True(snapshot.PowerIsChipOnly);
    }

    [Fact]
    public void A_card_counter_that_appears_mid_stream_never_borrows_the_chip_delta()
    {
        // The previous block had no card counter, this one does. Its card
        // energy is reported; its power waits one tick for a card delta
        // instead of passing the chip's 50 W off under the card's scope.
        var snapshot = SecondPoll(new FakeIntelTelemetryDevice(Sample(0, card: false), Sample(1)));

        Assert.Null(snapshot.PowerW);
        Assert.Null(snapshot.PowerAvgW);
        Assert.Equal(7300 / 3600.0, snapshot.EnergyWh);
        Assert.False(snapshot.PowerIsChipOnly);
    }

    [Fact]
    public void A_card_counter_reset_reports_no_power_rather_than_the_chip_figure()
    {
        // The card counter went backwards (driver reset) while the chip
        // counter moved on: an honest "—" for one tick, not 50 W as board power.
        var reset = Sample(1);
        reset.TotalCardEnergyCounter = Joules(10);
        var snapshot = SecondPoll(new FakeIntelTelemetryDevice(Sample(0), reset));

        Assert.Null(snapshot.PowerW);
    }

    [Fact]
    public void A_card_counter_that_advances_less_than_the_chip_is_not_trusted_as_the_card()
    {
        // "Supported" but stalled: the card's energy includes the chip's, so
        // a card delta below the chip delta cannot be the whole card. The poll
        // reports the chip — labelled as the chip — not 0 W of board power.
        var stalledBefore = Sample(0);
        stalledBefore.TotalCardEnergyCounter = Joules(0);
        var stalledAfter = Sample(1);
        stalledAfter.TotalCardEnergyCounter = Joules(0);
        var snapshot = SecondPoll(new FakeIntelTelemetryDevice(stalledBefore, stalledAfter));

        Assert.Equal(50.0, snapshot.PowerW);
        Assert.Equal(3650 / 3600.0, snapshot.EnergyWh);
        Assert.True(snapshot.PowerIsChipOnly);
    }

    [Fact]
    public void A_card_counter_equal_to_the_chip_still_counts_as_the_card()
    {
        // The guard is "less than", not "not more than": a card whose
        // non-chip draw rounds away is still the driver's whole-card figure.
        var before = Sample(0);
        before.TotalCardEnergyCounter = Joules(1000);
        var after = Sample(1);
        after.TotalCardEnergyCounter = Joules(1050);
        var snapshot = SecondPoll(new FakeIntelTelemetryDevice(before, after));

        Assert.Equal(50.0, snapshot.PowerW);
        Assert.False(snapshot.PowerIsChipOnly);
    }

    [Fact]
    public void No_energy_counter_at_all_reports_no_power_and_no_energy()
    {
        var empty = new CtlPowerTelemetry { TimeStamp = FakeIntelTelemetryDevice.Item(0, CtlUnits.TimeSeconds) };
        var later = new CtlPowerTelemetry { TimeStamp = FakeIntelTelemetryDevice.Item(1, CtlUnits.TimeSeconds) };
        var snapshot = SecondPoll(new FakeIntelTelemetryDevice(empty, later));

        Assert.Null(snapshot.PowerW);
        Assert.Null(snapshot.EnergyWh);
        Assert.Null(snapshot.PowerIsChipOnly);
    }

    [Fact]
    public void Bulk_vram_temperature_answers_when_no_memory_sensor_is_enumerated()
    {
        // R-19: the review's driver shape — 92 °C supported in the bulk
        // block, no memory temperature sensor handle — produced null, so
        // memory alerts and rules never fired.
        var snapshot = new IntelSensorSource(new FakeIntelTelemetryDevice(Sample(0, vramC: 92)), 0).Poll();

        Assert.Equal(92.0, snapshot.MemJunctionTempC);
        // The hottest VRAM sensor, not a memory-junction reading.
        Assert.True(snapshot.MemTempIsVramSensor);
    }

    [Fact]
    public void An_enumerated_memory_sensor_still_wins_over_the_bulk_value()
    {
        var device = new FakeIntelTelemetryDevice(Sample(0, vramC: 92));
        device.AddMemorySensor(81.5);
        var snapshot = new IntelSensorSource(device, 0).Poll();

        Assert.Equal(81.5, snapshot.MemJunctionTempC);
        // Documented as "the maximum temperature across all sensors in the
        // local memory": also a VRAM reading, labelled the same way.
        Assert.True(snapshot.MemTempIsVramSensor);
    }

    [Fact]
    public void Bulk_vram_temperature_fills_in_when_the_memory_sensor_read_fails()
    {
        var device = new FakeIntelTelemetryDevice(Sample(0, vramC: 92));
        device.AddMemorySensor(readingC: null);
        var source = new IntelSensorSource(device, 0);

        Assert.Equal(92.0, source.Poll().MemJunctionTempC);
    }

    [Fact]
    public void Unsupported_or_non_celsius_bulk_vram_temperature_stays_unavailable()
    {
        var unsupported = new IntelSensorSource(new FakeIntelTelemetryDevice(Sample(0)), 0).Poll();
        Assert.Null(unsupported.MemJunctionTempC);
        Assert.Null(unsupported.MemTempIsVramSensor);

        var wrongUnits = Sample(0);
        wrongUnits.VramCurrentTemperature = FakeIntelTelemetryDevice.Item(92, CtlUnits.Percent);
        Assert.Null(new IntelSensorSource(new FakeIntelTelemetryDevice(wrongUnits), 0).Poll().MemJunctionTempC);

        var notANumber = Sample(0);
        notANumber.VramCurrentTemperature = Celsius(double.NaN);
        Assert.Null(new IntelSensorSource(new FakeIntelTelemetryDevice(notANumber), 0).Poll().MemJunctionTempC);
    }

    [Fact]
    public void Scope_labels_reach_the_json_and_stay_out_of_it_when_unset()
    {
        // CLI --json and MCP get_telemetry serialize the snapshot itself: the
        // label rides along with the Intel numbers, while a snapshot that
        // states no scope (every NVIDIA one) serializes exactly as before.
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        var intel = SecondPoll(new FakeIntelTelemetryDevice(Sample(0, card: false, vramC: 70), Sample(1, card: false, vramC: 70)));
        string intelJson = System.Text.Json.JsonSerializer.Serialize(intel, options);
        Assert.Contains("\"powerIsChipOnly\":true", intelJson, StringComparison.Ordinal);
        Assert.Contains("\"memTempIsVramSensor\":true", intelJson, StringComparison.Ordinal);

        var nvidia = new GpuSnapshot { Timestamp = DateTimeOffset.UnixEpoch, DeviceIndex = 0, PowerW = 300, MemJunctionTempC = 80 };
        string nvidiaJson = System.Text.Json.JsonSerializer.Serialize(nvidia, options);
        Assert.DoesNotContain("powerIsChipOnly", nvidiaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("memTempIsVramSensor", nvidiaJson, StringComparison.Ordinal);
    }
}

/// <summary>
/// The Intel identity string: stable across reboots (full PCI location +
/// device id, never the per-boot LUID). Per-GPU state files keep only the
/// first 12 alphanumerics after the stripped vendor prefix, so the entire
/// PCI location must fit inside that budget — these tests pin exactly that.
/// </summary>
public class IntelIdentityTests
{
    [Fact]
    public void Two_identical_cards_in_different_slots_get_distinct_state_files()
    {
        // Same device id, different bus — as in a dual-Arc rig.
        string a = "INTEL-0000:03:00.0-E20B";
        string b = "INTEL-0000:04:00.0-E20B";
        Assert.NotEqual(
            Afterglow.Core.Tuning.AppliedStateStore.PathFor(a),
            Afterglow.Core.Tuning.AppliedStateStore.PathFor(b));
        Assert.NotEqual(
            Afterglow.Core.Tuning.VfCurveRecorder.PathFor(a, isPrimary: false),
            Afterglow.Core.Tuning.VfCurveRecorder.PathFor(b, isPrimary: false));
    }

    [Fact]
    public void Identical_cards_in_different_pci_domains_get_distinct_state_files()
    {
        // Multi-domain systems are exotic, but the domain leads the location
        // so even a same-BDF pair across domains cannot share a file.
        string a = "INTEL-0000:00:02.0-B082";
        string b = "INTEL-0001:00:02.0-B082";
        Assert.NotEqual(
            Afterglow.Core.Tuning.AppliedStateStore.PathFor(a),
            Afterglow.Core.Tuning.AppliedStateStore.PathFor(b));
    }

    [Fact]
    public void Shared_memory_planner_reserves_a_quarter_of_the_budget()
    {
        // 12 GiB budget, 1 GiB in use, plenty of free RAM → reserve 3 GiB, plan ≈ 8 GiB.
        long gib = 1024L * 1024 * 1024;
        long[] plan = Afterglow.Core.Stress.VramTest.PlanChunksShared(12 * gib, 1 * gib, 20 * gib);
        Assert.NotEmpty(plan);
        long total = plan.Sum();
        Assert.InRange(total, 7 * gib, 8 * gib);

        // A tight budget refuses instead of squeezing out a token test.
        Assert.Empty(Afterglow.Core.Stress.VramTest.PlanChunksShared(2 * gib, 2 * gib, 20 * gib));
    }

    [Fact]
    public void Shared_memory_planner_never_plans_past_free_physical_ram()
    {
        // A loaded 8 GiB machine: 5 GiB GPU budget but only 3 GiB physically
        // free. The budget math alone would plan 3.25 GiB; the physical cap
        // holds it to ~1.5 GiB so the test cannot page the system (and tested
        // bytes cannot silently round-trip through the pagefile).
        long gib = 1024L * 1024 * 1024;
        long[] plan = Afterglow.Core.Stress.VramTest.PlanChunksShared(
            5 * gib, gib / 4, 3 * gib);
        Assert.NotEmpty(plan);
        Assert.True(plan.Sum() <= 3 * gib - (long)(1.4 * gib));
    }

    [Fact]
    public void Validation_floors_follow_the_device_not_the_nvidia_defaults()
    {
        // A 150 MHz clamp is legitimate on Intel (domain floor 100 MHz) but
        // was rejected by the NVML-era 210 MHz schema floor.
        var profile = new Afterglow.Core.Profiles.TuningProfile { Name = "clamp", LockedCoreClockMHz = 150 };
        Assert.NotNull(profile.Validate());
        Assert.Null(profile.Validate(lockFloorMHz: 100));

        // Low-power Arc parts report power minimums below the 50 W default.
        var lowPower = new Afterglow.Core.Profiles.TuningProfile { Name = "lp", PowerLimitW = 25 };
        Assert.NotNull(lowPower.Validate());
        Assert.Null(lowPower.Validate(powerFloorW: 15));
    }

    [Fact]
    public void Intel_and_nvidia_state_files_cannot_collide()
    {
        // After prefix stripping, Intel suffixes start with 'i' and NVML UUIDs
        // are hex — disjoint namespaces by construction.
        string intel = "INTEL-0000:00:02.0-B082";
        string nvidia = "GPU-2b6ae74e-59c2-11ee-8c99-0242ac120002";
        Assert.NotEqual(
            Afterglow.Core.Tuning.AppliedStateStore.PathFor(intel),
            Afterglow.Core.Tuning.AppliedStateStore.PathFor(nvidia));
    }
}
