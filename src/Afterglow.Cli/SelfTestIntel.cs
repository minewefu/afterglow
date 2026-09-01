using Afterglow.Core.Interop.Igcl;
using Afterglow.Core.Interop.LevelZero;

namespace Afterglow.Cli;

/// <summary>
/// Intel sections of the self-test: IGCL (control + telemetry) and Level Zero
/// Sysman (second telemetry source). Read-only throughout — every probed
/// capability is reported truthfully, including the ones this device lacks.
/// </summary>
internal static class SelfTestIntel
{
    public static bool DumpIgcl()
    {
        Console.WriteLine();
        Console.WriteLine("=== Intel IGCL ===");

        using var igcl = IgclApi.TryCreate(out var initStatus);
        if (igcl is null)
        {
            Console.WriteLine($"  IGCL unavailable: {initStatus}");
            return false;
        }

        Console.WriteLine($"  IGCL initialized. Runtime version {igcl.SupportedVersion >> 16}.{igcl.SupportedVersion & 0xFFFF}");

        var devices = igcl.GetDevices();
        Console.WriteLine($"  Graphics adapters: {devices.Count}");

        foreach (var device in devices)
        {
            DumpIgclDevice(device);
        }

        return true;
    }

    private static void DumpIgclDevice(IgclDevice d)
    {
        Console.WriteLine();
        Console.WriteLine($"--- Intel GPU {d.Index}: {d.Name} ---");
        Console.WriteLine($"  PCI ids:     0x{d.PciVendorId:X4}:0x{d.PciDeviceId:X4}{(d.IsIntegrated ? " (integrated)" : " (discrete)")}");
        Console.WriteLine($"  BDF:         {d.Bdf.Bus:x2}:{d.Bdf.Device:x2}.{d.Bdf.Function}");
        Console.WriteLine($"  Driver:      {d.DriverVersion}");
        Console.WriteLine($"  LUID:        0x{d.Luid:X}");

        ReportCtl("PCI properties", d.TryGetPciProperties(out var pci),
            $"gen{pci.MaxSpeed.Gen} x{pci.MaxSpeed.Width}, ReBAR {(pci.ResizableBarEnabled != 0 ? "on" : "off")}");

        DumpIgclTelemetry(d);
        DumpIgclComponents(d);
        DumpIgclOverclock(d);
    }

    private static void DumpIgclTelemetry(IgclDevice d)
    {
        // Two snapshots ~150 ms apart: the energy/activity counters are
        // monotonic, so watts and utilization only exist as deltas.
        var rc1 = d.TryGetPowerTelemetry(out var t1);
        if (rc1 != CtlResult.Success)
        {
            ReportCtl("Power telemetry", rc1, string.Empty);
            return;
        }

        Thread.Sleep(150);
        var rc2 = d.TryGetPowerTelemetry(out var t2);
        if (rc2 != CtlResult.Success)
        {
            ReportCtl("Power telemetry (2nd)", rc2, string.Empty);
            return;
        }

        Console.WriteLine($"  Power telemetry            OK       (Version {t2.Version})");
        double dt = t2.TimeStamp.AsDouble() - t1.TimeStamp.AsDouble();
        // Only ever used as a delta: the header documents epoch seconds, but the
        // verified driver counts from boot (see docs/research/intel-driver-apis.md).
        Item("timeStamp", t2.TimeStamp, "s");
        Counter("gpuEnergyCounter -> W", t1.GpuEnergyCounter, t2.GpuEnergyCounter, dt);
        Item("gpuVoltage", t2.GpuVoltage);
        Item("gpuCurrentClockFrequency", t2.GpuCurrentClockFrequency);
        Item("gpuCurrentTemperature", t2.GpuCurrentTemperature);
        Activity("globalActivityCounter -> %", t1.GlobalActivityCounter, t2.GlobalActivityCounter, dt);
        Activity("renderComputeActivity -> %", t1.RenderComputeActivityCounter, t2.RenderComputeActivityCounter, dt);
        Activity("mediaActivityCounter -> %", t1.MediaActivityCounter, t2.MediaActivityCounter, dt);
        Console.WriteLine($"    throttle flags           power={Flag(t2.GpuPowerLimited)} temp={Flag(t2.GpuTemperatureLimited)} current={Flag(t2.GpuCurrentLimited)} voltage={Flag(t2.GpuVoltageLimited)} util={Flag(t2.GpuUtilizationLimited)}");
        Counter("vramEnergyCounter -> W", t1.VramEnergyCounter, t2.VramEnergyCounter, dt);
        Item("vramVoltage", t2.VramVoltage);
        Item("vramCurrentClockFrequency", t2.VramCurrentClockFrequency);
        Item("vramCurrentEffectiveFreq", t2.VramCurrentEffectiveFrequency);
        Item("vramCurrentTemperature", t2.VramCurrentTemperature);
        Counter("totalCardEnergyCounter -> W", t1.TotalCardEnergyCounter, t2.TotalCardEnergyCounter, dt);
        for (int i = 0; i < 5; i++)
        {
            if (t2.FanSpeed[i].Supported != 0)
            {
                Item($"fanSpeed[{i}]", t2.FanSpeed[i]);
            }
        }

        Item("gpuVrTemp", t2.GpuVrTemp);
        Item("vramVrTemp", t2.VramVrTemp);
        Item("saVrTemp", t2.SaVrTemp);
        Item("gpuEffectiveClock", t2.GpuEffectiveClock);
        Item("gpuOverVoltagePercent", t2.GpuOverVoltagePercent);
        Item("gpuPowerPercent", t2.GpuPowerPercent);
        Item("gpuTemperaturePercent", t2.GpuTemperaturePercent);
        Item("vramReadBandwidth", t2.VramReadBandwidth);
        Item("vramWriteBandwidth", t2.VramWriteBandwidth);
    }

    private static void DumpIgclComponents(IgclDevice d)
    {
        var freqDomains = d.GetFrequencyDomains();
        Console.WriteLine($"  Frequency domains          {(freqDomains.Count > 0 ? "OK  " : "[none]"),-8} {freqDomains.Count}");
        foreach (var (handle, props) in freqDomains)
        {
            string control = props.CanControl != 0 ? "controllable" : "read-only";
            Console.WriteLine($"    {props.Type,-24} {props.Min:F0}..{props.Max:F0} MHz, {control}");
            if (IgclDevice.TryGetFrequencyState(handle, out var state) == CtlResult.Success)
            {
                Console.WriteLine($"      actual {Mhz(state.Actual)}, request {Mhz(state.Request)}, tdp-max {Mhz(state.Tdp)}, efficient {Mhz(state.Efficient)}, {Volts(state.CurrentVoltage)}, throttle [{state.ThrottleReasons}]");
            }

            if (IgclDevice.TryGetFrequencyRange(handle, out var range) == CtlResult.Success)
            {
                Console.WriteLine($"      range clamp {range.Min:F0}..{range.Max:F0} MHz");
            }
        }

        var temps = d.GetTemperatureSensors();
        Console.WriteLine($"  Temperature sensors        {(temps.Count > 0 ? "OK  " : "[none]"),-8} {temps.Count}");
        foreach (var (handle, props) in temps)
        {
            var rc = IgclDevice.TryGetTemperature(handle, out double c);
            Console.WriteLine($"    {props.Type,-24} {(rc == CtlResult.Success ? $"{c:F1} C" : $"[{rc}]")} (max {props.MaxTemperature:F0} C)");
        }

        var modules = d.GetMemoryModules();
        Console.WriteLine($"  Memory modules             {(modules.Count > 0 ? "OK  " : "[none]"),-8} {modules.Count}");
        foreach (var (handle, props) in modules)
        {
            string location = props.Location == CtlMemLocation.Device ? "DEDICATED (device)" : "SHARED (system)";
            Console.WriteLine($"    {location}, type {props.Type}, bus {props.BusWidth}-bit x{props.NumChannels}, physical {Gib(props.PhysicalSize)}");
            if (IgclDevice.TryGetMemoryState(handle, out var state) == CtlResult.Success)
            {
                Console.WriteLine($"      used {Gib(state.Total - state.Free)} / {Gib(state.Total)}");
            }

            var bwRc = IgclDevice.TryGetMemoryBandwidth(handle, out var bw);
            Console.WriteLine(bwRc == CtlResult.Success
                ? $"      bandwidth max {bw.MaxBandwidth / 1e9:F1} GB/s"
                : $"      bandwidth [{bwRc}]");
        }

        var engines = d.GetEngineGroups();
        Console.WriteLine($"  Engine groups              {(engines.Count > 0 ? "OK  " : "[none]"),-8} {engines.Count}");
        foreach (var (handle, props) in engines)
        {
            var rcA = IgclDevice.TryGetEngineActivity(handle, out var s1);
            if (rcA == CtlResult.Success)
            {
                Thread.Sleep(100);
                var rcB = IgclDevice.TryGetEngineActivity(handle, out var s2);
                if (rcB == CtlResult.Success && s2.Timestamp > s1.Timestamp)
                {
                    double util = 100.0 * (s2.ActiveTime - s1.ActiveTime) / (s2.Timestamp - s1.Timestamp);
                    Console.WriteLine($"    {props.Type,-24} {util:F1}% busy");
                }
                else
                {
                    Console.WriteLine($"    {props.Type,-24} {(rcB == CtlResult.Success ? "[no delta]" : $"[{rcB}]")}");
                }

                continue;
            }

            Console.WriteLine($"    {props.Type,-24} [{rcA}]");
        }

        var fans = d.GetFans();
        Console.WriteLine($"  Fans                       {(fans.Count > 0 ? "OK  " : "[none]"),-8} {fans.Count}");
        foreach (var (handle, props) in fans)
        {
            var rcRpm = IgclDevice.TryGetFanState(handle, CtlFanSpeedUnits.Rpm, out int rpm);
            string reading = rcRpm == CtlResult.Success
                ? (rpm >= 0 ? $"{rpm} RPM" : "unmeasurable (-1)")
                : $"[{rcRpm}]";
            Console.WriteLine($"    canControl={props.CanControl != 0}, maxRPM={props.MaxRpm}, tablePoints={props.MaxPoints}, now {reading}");
        }

        var powerDomains = d.GetPowerDomains();
        Console.WriteLine($"  Power domains              {(powerDomains.Count > 0 ? "OK  " : "[none]"),-8} {powerDomains.Count}");
        foreach (var (handle, props) in powerDomains)
        {
            Console.WriteLine($"    canControl={props.CanControl != 0}, default {Mw(props.DefaultLimitMw)}, range {Mw(props.MinLimitMw)}..{Mw(props.MaxLimitMw)}");

            var rcE = IgclDevice.TryGetEnergyCounter(handle, out var e1);
            if (rcE == CtlResult.Success)
            {
                Thread.Sleep(100);
                if (IgclDevice.TryGetEnergyCounter(handle, out var e2) == CtlResult.Success && e2.TimestampUs > e1.TimestampUs)
                {
                    double watts = (e2.EnergyUj - e1.EnergyUj) / (double)(e2.TimestampUs - e1.TimestampUs);
                    Console.WriteLine($"      energy counter -> {watts:F1} W");
                }
            }
            else
            {
                Console.WriteLine($"      energy counter [{rcE}]");
            }

            var rcL = IgclDevice.TryGetPowerLimits(handle, out var limits);
            if (rcL == CtlResult.Success)
            {
                Console.WriteLine($"      PL1 sustained: {(limits.Sustained.Enabled != 0 ? Mw(limits.Sustained.PowerMw) : "disabled")} (tau {limits.Sustained.IntervalMs} ms)");
                Console.WriteLine($"      PL2 burst:     {(limits.Burst.Enabled != 0 ? Mw(limits.Burst.PowerMw) : "disabled")}");
                Console.WriteLine($"      PL4 peak:      AC {Mw(limits.Peak.PowerAcMw)}, DC {(limits.Peak.PowerDcMw < 0 ? "no battery" : Mw(limits.Peak.PowerDcMw))}");
            }
            else
            {
                Console.WriteLine($"      limits [{rcL}]");
            }
        }
    }

    private static void DumpIgclOverclock(IgclDevice d)
    {
        var rc = d.TryGetOcProperties(out var oc);
        if (rc != CtlResult.Success)
        {
            ReportCtl("Overclock properties", rc, string.Empty);
            return;
        }

        Console.WriteLine($"  Overclock properties       OK       supported={oc.Supported != 0} (Version {oc.Version})");
        Knob("gpuFrequencyOffset", oc.GpuFrequencyOffset);
        Knob("gpuVoltageOffset", oc.GpuVoltageOffset);
        Knob("powerLimit", oc.PowerLimit);
        Knob("temperatureLimit", oc.TemperatureLimit);
        Knob("vramMemSpeedLimit", oc.VramMemSpeedLimit);
        Knob("gpuVFCurveVoltageLimit", oc.GpuVfCurveVoltageLimit);
        Knob("gpuVFCurveFrequencyLimit", oc.GpuVfCurveFrequencyLimit);

        // Current values, read-only. V2 first (Arc-era), V1 as fallback so the
        // report shows which generation of the API this driver answers. A V2
        // value's unit is defined only by the knob's capability block — when the
        // knob is unsupported the block is never written and its zeroed Units
        // field would masquerade as FrequencyMhz, so print '?' instead.
        ReportCtl("Freq offset (V2)", d.TryGetGpuFrequencyOffsetV2(out double f2), $"{f2:F0} [{KnobUnit(oc.GpuFrequencyOffset)}]");
        ReportCtl("Freq offset (V1)", d.TryGetGpuFrequencyOffset(out double f1), $"{f1:F0} MHz");
        ReportCtl("Voltage offset (V2)", d.TryGetGpuVoltageOffsetV2(out double v2), $"{v2:F0} [{KnobUnit(oc.GpuVoltageOffset)}]");
        ReportCtl("Voltage offset (V1)", d.TryGetGpuVoltageOffset(out double v1), $"{v1:F0} mV");
        ReportCtl("GPU lock", d.TryGetGpuLock(out var pair),
            pair.Voltage == 0 && pair.Frequency == 0 ? "not locked" : $"{pair.Frequency:F0} MHz @ {pair.Voltage:F0} mV");
        ReportCtl("OC power limit (V2)", d.TryGetOcPowerLimitV2(out double p2), $"{p2:F0} [{KnobUnit(oc.PowerLimit)}]");
        ReportCtl("OC power limit (V1)", d.TryGetOcPowerLimit(out double p1), $"{p1 / 1000.0:F1} W");
        ReportCtl("Temp limit (V2)", d.TryGetOcTemperatureLimitV2(out double t2), $"{t2:F0} [{KnobUnit(oc.TemperatureLimit)}]");
        ReportCtl("Temp limit (V1)", d.TryGetOcTemperatureLimit(out double t1), $"{t1:F0} C");

        var stockRc = d.TryReadVfCurve(CtlVfCurveType.Stock, CtlVfCurveDetails.Elaborate, out var stock);
        ReportCtl("VF curve (stock)", stockRc, $"{stock.Length} points");
        var liveRc = d.TryReadVfCurve(CtlVfCurveType.Live, CtlVfCurveDetails.Elaborate, out var live);
        ReportCtl("VF curve (live)", liveRc, $"{live.Length} points");
        if (liveRc == CtlResult.Success && live.Length > 0)
        {
            foreach (var point in live.Where((_, i) => i % Math.Max(1, live.Length / 8) == 0).Take(8))
            {
                Console.WriteLine($"    {point.VoltageMv,7} mV -> {point.FrequencyMhz,7} MHz");
            }
        }
    }

    public static bool DumpSysman()
    {
        Console.WriteLine();
        Console.WriteLine("=== Level Zero Sysman (read-only probe) ===");

        var zes = ZesApi.TryCreate(out var initStatus);
        if (zes is null)
        {
            Console.WriteLine($"  Sysman unavailable: {initStatus}");
            return false;
        }

        Console.WriteLine($"  Sysman initialized. Devices: {zes.Devices.Count}");

        for (int i = 0; i < zes.Devices.Count; i++)
        {
            DumpSysmanDevice(zes.Devices[i], i);
        }

        return true;
    }

    private static void DumpSysmanDevice(ZesDevice d, int index)
    {
        Console.WriteLine();
        var pciRc = d.TryGetPciProperties(out var pci);
        string bdf = pciRc == ZeResult.Success
            ? $"{pci.Address.Domain:x4}:{pci.Address.Bus:x2}:{pci.Address.Device:x2}.{pci.Address.Function}"
            : $"[{pciRc}]";
        Console.WriteLine($"--- Sysman device {index} (BDF {bdf}) ---");

        var powerDomains = d.GetPowerDomains();
        Console.WriteLine($"  Power domains              {(powerDomains.Count > 0 ? "OK  " : "[none]"),-8} {powerDomains.Count}");
        foreach (var (handle, props) in powerDomains)
        {
            var rcE = ZesDevice.TryGetEnergyCounter(handle, out var e1);
            string power = $"[{rcE}]";
            if (rcE == ZeResult.Success)
            {
                Thread.Sleep(100);
                if (ZesDevice.TryGetEnergyCounter(handle, out var e2) == ZeResult.Success && e2.TimestampUs > e1.TimestampUs)
                {
                    power = $"{(e2.EnergyUj - e1.EnergyUj) / (double)(e2.TimestampUs - e1.TimestampUs):F1} W";
                }
            }

            Console.WriteLine($"    canControl={props.CanControl != 0}, default {Mw(props.DefaultLimitMw)}, range {Mw(props.MinLimitMw)}..{Mw(props.MaxLimitMw)}, now {power}");
        }

        var freqDomains = d.GetFrequencyDomains();
        Console.WriteLine($"  Frequency domains          {(freqDomains.Count > 0 ? "OK  " : "[none]"),-8} {freqDomains.Count}");
        foreach (var (handle, props) in freqDomains)
        {
            Console.WriteLine($"    {props.Type,-24} {props.Min:F0}..{props.Max:F0} MHz, canControl={props.CanControl != 0}");
            if (ZesDevice.TryGetFrequencyState(handle, out var state) == ZeResult.Success)
            {
                Console.WriteLine($"      actual {Mhz(state.Actual)}, request {Mhz(state.Request)}, tdp-max {Mhz(state.Tdp)}, efficient {Mhz(state.Efficient)}, {Volts(state.CurrentVoltage)}, throttle [{state.ThrottleReasons}]");
            }
        }

        var temps = d.GetTemperatureSensors();
        Console.WriteLine($"  Temperature sensors        {(temps.Count > 0 ? "OK  " : "[none]"),-8} {temps.Count}");
        foreach (var (handle, props) in temps)
        {
            var rc = ZesDevice.TryGetTemperature(handle, out double c);
            Console.WriteLine($"    {props.Type,-24} {(rc == ZeResult.Success ? $"{c:F1} C" : $"[{rc}]")}");
        }

        var modules = d.GetMemoryModules();
        Console.WriteLine($"  Memory modules             {(modules.Count > 0 ? "OK  " : "[none]"),-8} {modules.Count}");
        foreach (var (handle, props) in modules)
        {
            string location = props.Location == ZesMemLocation.Device ? "DEDICATED (device)" : "SHARED (system)";
            Console.WriteLine($"    {location}, type {props.Type}, bus {props.BusWidth}-bit x{props.NumChannels}, physical {Gib(props.PhysicalSize)}");
            if (ZesDevice.TryGetMemoryState(handle, out var state) == ZeResult.Success)
            {
                Console.WriteLine($"      used {Gib(state.Total - state.Free)} / {Gib(state.Total)}");
            }

            var bwRc = ZesDevice.TryGetMemoryBandwidth(handle, out var bw);
            Console.WriteLine(bwRc == ZeResult.Success
                ? $"      bandwidth max {bw.MaxBandwidth / 1e9:F1} GB/s"
                : $"      bandwidth [{bwRc}]");
        }

        var engines = d.GetEngineGroups();
        Console.WriteLine($"  Engine groups              {(engines.Count > 0 ? "OK  " : "[none]"),-8} {engines.Count}");
        foreach (var (handle, props) in engines)
        {
            var rcA = ZesDevice.TryGetEngineActivity(handle, out var s1);
            if (rcA == ZeResult.Success)
            {
                Thread.Sleep(100);
                var rcB = ZesDevice.TryGetEngineActivity(handle, out var s2);
                if (rcB == ZeResult.Success && s2.TimestampUs > s1.TimestampUs)
                {
                    double util = 100.0 * (s2.ActiveTime - s1.ActiveTime) / (s2.TimestampUs - s1.TimestampUs);
                    Console.WriteLine($"    {props.Type,-24} {util:F1}% busy");
                }
                else
                {
                    Console.WriteLine($"    {props.Type,-24} {(rcB == ZeResult.Success ? "[no delta]" : $"[{rcB}]")}");
                }

                continue;
            }

            Console.WriteLine($"    {props.Type,-24} [{rcA}]");
        }

        Console.WriteLine($"  Fans                       {d.GetFanCount()} (0 expected on EC-controlled handhelds)");
    }

    // --- Formatting helpers ---------------------------------------------------

    private static void ReportCtl(string label, CtlResult rc, string value)
    {
        bool ok = rc == CtlResult.Success;
        Console.WriteLine($"  {label,-26} {(ok ? "OK  " : $"[{rc}]"),-24} {(ok ? value : string.Empty)}");
    }

    private static void Item(string label, in CtlTelemetryItem item, string? unitOverride = null)
    {
        Console.WriteLine(item.Supported != 0
            ? $"    {label,-24} {item.AsDouble():F1} {unitOverride ?? Unit(item.Units)}"
            : $"    {label,-24} [unsupported]");
    }

    private static void Counter(string label, in CtlTelemetryItem before, in CtlTelemetryItem after, double dtSeconds)
    {
        if (before.Supported == 0 || after.Supported == 0)
        {
            Console.WriteLine($"    {label,-24} [unsupported]");
            return;
        }

        if (dtSeconds <= 0)
        {
            Console.WriteLine($"    {label,-24} [no time delta]");
            return;
        }

        // Energy counters are typically joules; delta-J / delta-s = watts.
        double delta = after.AsDouble() - before.AsDouble();
        Console.WriteLine($"    {label,-24} {delta / dtSeconds:F1} W (counter unit {Unit(after.Units)})");
    }

    private static void Activity(string label, in CtlTelemetryItem before, in CtlTelemetryItem after, double dtSeconds)
    {
        if (before.Supported == 0 || after.Supported == 0)
        {
            Console.WriteLine($"    {label,-24} [unsupported]");
            return;
        }

        if (dtSeconds <= 0)
        {
            Console.WriteLine($"    {label,-24} [no time delta]");
            return;
        }

        double busy = 100.0 * (after.AsDouble() - before.AsDouble()) / dtSeconds;
        Console.WriteLine($"    {label,-24} {busy:F1}%");
    }

    private static void Knob(string label, in CtlOcControlInfo info)
    {
        Console.WriteLine(info.Supported != 0
            ? $"    {label,-24} {info.Min:F0}..{info.Max:F0} step {info.Step:F1} default {info.Default:F0} [{Unit(info.Units)}]{(info.Relative != 0 ? " relative" : "")}{(info.HasReference != 0 ? $" ref {info.Reference:F0}" : "")}"
            : $"    {label,-24} [unsupported]");
    }

    private static string Unit(CtlUnits units) => units switch
    {
        CtlUnits.FrequencyMhz => "MHz",
        CtlUnits.OperationsGts => "GT/s",
        CtlUnits.OperationsMts => "MT/s",
        CtlUnits.VoltageVolts => "V",
        CtlUnits.PowerWatts => "W",
        CtlUnits.TemperatureCelsius => "C",
        CtlUnits.EnergyJoules => "J",
        CtlUnits.TimeSeconds => "s",
        CtlUnits.MemoryBytes => "bytes",
        CtlUnits.AngularSpeedRpm => "RPM",
        CtlUnits.PowerMilliwatts => "mW",
        CtlUnits.Percent => "%",
        CtlUnits.MemSpeedGbps => "GB/s",
        CtlUnits.VoltageMillivolts => "mV",
        CtlUnits.BandwidthMbps => "MB/s",
        _ => "?",
    };

    private static string KnobUnit(in CtlOcControlInfo info) => info.Supported != 0 ? Unit(info.Units) : "?";

    private static string Flag(byte value) => value != 0 ? "YES" : "no";

    private static string Mhz(double value) => value < 0 ? "n/a" : $"{value:F0} MHz";

    private static string Volts(double value) => value < 0 ? "voltage n/a" : $"{value * 1000.0:F0} mV";

    private static string Mw(int milliwatts) => milliwatts < 0 ? "n/a" : $"{milliwatts / 1000.0:F1} W";

    private static string Gib(ulong bytes) => $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GiB";
}
