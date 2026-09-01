using Afterglow.Core.Interop.Igcl;
using Afterglow.Core.Telemetry;

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
