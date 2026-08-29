using System.Security.Principal;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;

namespace Afterglow.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        return command switch
        {
            "selftest" => SelfTest.Run(),
            "monitor" => MonitorCommand.Run(args),
            "caps" => TuneCommands.Caps(args),
            "get" => TuneCommands.Get(args),
            "set" => TuneCommands.Set(args),
            "reset" => TuneCommands.Reset(args),
            "fps" => FpsCommand.Run(args),
            "stress" => StressCommand.Run(args),
            "vram" => VramCommand.Run(args),
            "certify" => CertifyCommand.Run(args),
            "drs" => DrsCommand.Run(args),
            "vfcurve" => VfCurveCommand.Run(args),
            "vfpoints" => VfPointsCommand.Run(args),
            "mcp" => McpCommand.Run(args),
            "help" or "--help" or "-h" => Help(),
            _ => Fail($"Unknown command '{command}'. Run 'afterglow-cli help'."),
        };
    }

    private static int Help()
    {
        Console.WriteLine("""
            afterglow-cli — command-line companion for Afterglow

            Commands:
              selftest                      Probe the GPU and report per-capability support.
              monitor [--interval ms]       Live sensor readout (Ctrl+C to stop).
                      [--csv file] [--once]
              caps [--gpu N]                Show driver-reported tuning ranges.
              get [--gpu N]                 Show currently applied offsets/limits.
              set [--gpu N] [options]       Apply tuning (requires administrator):
                  --core-offset MHZ  --mem-offset MHZ  --power-limit W
                  --lock-clock MHZ|off  --voltage-boost PCT  --temp-limit C
                  --fan auto|PCT
              reset [--gpu N]               Restore all driver defaults.
              fps [--seconds N]             Capture FPS/frametimes for all presenting apps.
              stress [--seconds N]          Burn test with bit-exact error detection.
                     [--pattern P]          sustained (default) | transitions | excursions
                     [--intensity N] [--gpu N]
              vram [--seconds N] [--gpu N]  Full-capacity VRAM test: fills and verifies as
                                            much of the card as the OS safely allows.
              certify --profile NAME        Apply a saved profile, then run all four
                      [--seconds N] [--gpu N]  stability modes against it; each pass is stamped
                                            into the profile, all four = marked stable (admin).
              vfcurve [--probe]             Record and print the measured voltage/frequency
                      [--seconds N] [--gpu N]  curve. --probe locks each clock step under load
                      [--load] [--fresh]    and maps the full curve in ~1 min (admin).
              vfpoints [--gpu N]            Per-point V/F curve control (verified on RTX 50,
                       [--set "I=MHZ,..."]  expected on 20/30/40). No args = list the stored
                       [--flatten MV:MHZ]   table; --flatten = classic curve undervolt at the
                       [--clear]            point nearest MV (admin for writes).
              mcp [--gpu N]                 Model Context Protocol server over stdio, so AI
                                            agents can monitor, tune, and stability-test the
                                            GPU with typed tools (run elevated for writes).
                                            --gpu binds the whole server to one card.

            On multi-GPU systems, --gpu binds stress/VRAM work to that card's exact
            D3D adapter (matched by PCI bus, never by adapter order).

            caps, get, and monitor --once accept --json for machine-readable output.
              help                          Show this help.
            """);
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}

internal static class SelfTest
{
    public static int Run()
    {
        Console.WriteLine("Afterglow self-test");
        Console.WriteLine($"  Elevated: {IsElevated()}");
        Console.WriteLine();

        using var nvml = NvmlApi.TryCreate(out var initStatus);
        if (nvml is null)
        {
            Console.Error.WriteLine($"NVML initialization failed: {initStatus}");
            return 1;
        }

        Console.WriteLine($"NVML initialized. Driver {nvml.GetDriverVersion()}, NVML {nvml.GetNvmlVersion()}");

        var devices = nvml.GetDevices();
        Console.WriteLine($"Devices: {devices.Count}");

        foreach (var device in devices)
        {
            DumpDevice(device);
        }

        DumpNvapi(devices);
        return 0;
    }

    private static void DumpNvapi(IReadOnlyList<NvmlDevice> nvmlDevices)
    {
        Console.WriteLine();
        Console.WriteLine("=== NVAPI (read-only probe) ===");

        var nvapi = NvapiApi.TryCreate(out var initStatus);
        if (nvapi is null)
        {
            Console.WriteLine($"  NVAPI unavailable: {initStatus}");
            return;
        }

        var gpus = nvapi.GetGpus();
        Console.WriteLine($"  Physical GPUs: {gpus.Count}");

        foreach (var gpu in gpus)
        {
            // Give the channel mapper the NVML architecture (match by order; refined by bus id later).
            if (nvmlDevices.Count > 0 && nvmlDevices[0].TryGetArchitecture(out uint arch) == NvmlReturn.Success)
            {
                gpu.Architecture = arch;
            }

            Console.WriteLine($"  --- {gpu.GetName() ?? "<unnamed>"} (arch {gpu.Architecture}) ---");
            ReportNv("Bus id", gpu.TryGetBusId(out uint busId), $"{busId}");
            ReportNv("Tach RPM", gpu.TryGetTachRpm(out int tach), $"{tach} RPM");

            var (hotSpot, memJunction) = gpu.GetPrivateThermals();
            Console.WriteLine($"  {"Hot spot",-26} {(hotSpot is null ? "[unavailable]" : "OK  "),-24} {(hotSpot is double h ? $"{h:F1} C" : string.Empty)}");
            Console.WriteLine($"  {"Memory junction",-26} {(memJunction is null ? "[unavailable]" : "OK  "),-24} {(memJunction is double m ? $"{m:F1} C" : string.Empty)}");

            ReportNv("Core voltage", gpu.TryGetCoreVoltageMv(out double mv), $"{mv:F1} mV");
            ReportNv("Voltage boost", gpu.TryGetVoltageBoostPercent(out uint boost), $"{boost}%");
            ReportNv("Temp limit range", gpu.TryGetTempLimitRange(out int tMin, out int tDef, out int tMax),
                $"{tMin}..{tMax} C (default {tDef})");
            ReportNv("Temp limit current", gpu.TryGetTempLimit(out int tCur), $"{tCur} C");
            ReportNv("Util domains", gpu.TryGetUtilizationDomains(out int ug, out int uf, out int uv, out int ub),
                $"gpu {ug}%, fb {uf}%, vid {uv}%, bus {ub}%");

            var vfCurve = gpu.GetVfCurve();
            Console.WriteLine($"  {"V/F curve",-26} {(vfCurve.Count > 0 ? "OK  " : "[unavailable]"),-24} {(vfCurve.Count > 0 ? $"{vfCurve.Count} points" : string.Empty)}");
            if (vfCurve.Count > 0)
            {
                foreach (var point in vfCurve.Where((_, i) => i % Math.Max(1, vfCurve.Count / 8) == 0).Take(8))
                {
                    Console.WriteLine($"    {point.VoltageMv,7:F1} mV -> {point.ClockMHz,7:F0} MHz");
                }
            }

            var fanRc = gpu.TryGetFanStatus(out var fans);
            ReportNv("Fan coolers", fanRc, $"{fans.Count} fans");
            foreach (var fan in fans)
            {
                Console.WriteLine($"    cooler {fan.CoolerId}: {fan.Rpm} RPM, level {fan.Level}% (range {fan.MinLevel}..{fan.MaxLevel}%)");
            }
        }
    }

    private static void ReportNv(string label, NvapiStatus rc, string value)
    {
        string status = rc == NvapiStatus.Ok ? "OK  " : $"[{rc}]";
        Console.WriteLine($"  {label,-26} {status,-24} {(rc == NvapiStatus.Ok ? value : string.Empty)}");
    }

    private static void DumpDevice(NvmlDevice d)
    {
        Console.WriteLine();
        Console.WriteLine($"--- GPU {d.Index}: {d.GetName() ?? "<name unavailable>"} ---");
        Console.WriteLine($"  UUID:        {d.GetUuid()}");
        Console.WriteLine($"  VBIOS:       {d.GetVbiosVersion()}");
        Console.WriteLine($"  Board P/N:   {d.GetBoardPartNumber() ?? "<n/a>"}");

        Report("Architecture", d.TryGetArchitecture(out uint arch), $"{arch}");

        Report("Temp GPU", d.TryGetTemperature(NvmlTemperatureSensor.Gpu, out uint temp), $"{temp} C");
        foreach (NvmlTemperatureThreshold t in Enum.GetValues<NvmlTemperatureThreshold>())
        {
            Report($"TempThreshold {t}", d.TryGetTemperatureThreshold(t, out uint tt), $"{tt} C");
        }

        foreach (NvmlClockType c in Enum.GetValues<NvmlClockType>())
        {
            Report($"Clock {c}", d.TryGetClock(c, out uint mhz), $"{mhz} MHz");
            Report($"MaxClock {c}", d.TryGetMaxClock(c, out uint maxMhz), $"{maxMhz} MHz");
        }

        Report("BoostMax Graphics",
            d.TryGetClockById(NvmlClockType.Graphics, NvmlClockId.CustomerBoostMax, out uint boost), $"{boost} MHz");

        Report("Utilization", d.TryGetUtilization(out var util), $"gpu {util.Gpu}%, mem {util.Memory}%");
        Report("Encoder util", d.TryGetEncoderUtilization(out uint enc), $"{enc}%");
        Report("Decoder util", d.TryGetDecoderUtilization(out uint dec), $"{dec}%");

        Report("Memory", d.TryGetMemoryInfo(out var mem),
            $"{mem.Used / (1024 * 1024)} / {mem.Total / (1024 * 1024)} MiB used");
        Report("BAR1", d.TryGetBar1MemoryInfo(out var bar1),
            $"{bar1.Used / (1024 * 1024)} / {bar1.Total / (1024 * 1024)} MiB used");

        Report("Power usage", d.TryGetPowerUsage(out uint powerMw), $"{powerMw / 1000.0:F1} W");
        Report("Power limit (enforced)", d.TryGetEnforcedPowerLimit(out uint limitMw), $"{limitMw / 1000.0:F1} W");
        Report("Power limit constraints", d.TryGetPowerLimitConstraints(out uint minMw, out uint maxMw),
            $"{minMw / 1000.0:F0}..{maxMw / 1000.0:F0} W");
        Report("Power limit default", d.TryGetDefaultPowerLimit(out uint defMw), $"{defMw / 1000.0:F0} W");
        Report("Total energy", d.TryGetTotalEnergyConsumption(out ulong mj), $"{mj / 1000.0 / 3600.0:F1} Wh");

        Report("Perf state", d.TryGetPerformanceState(out uint pstate), $"P{pstate}");
        Report("Throttle reasons", d.TryGetClocksEventReasons(out var reasons), reasons.ToString());

        Report("Num fans", d.TryGetNumFans(out uint fans), $"{fans}");
        Report("Fan speed (legacy)", d.TryGetFanSpeed(out uint fanPct), $"{fanPct}%");
        for (uint f = 0; f < Math.Min(fans, 8); f++)
        {
            Report($"Fan[{f}] speed", d.TryGetFanSpeed(f, out uint pct), $"{pct}%");
            Report($"Fan[{f}] policy", d.TryGetFanControlPolicy(f, out var policy), policy.ToString());
        }

        Report("Fan min/max", d.TryGetMinMaxFanSpeed(out uint fanMin, out uint fanMax), $"{fanMin}..{fanMax}%");

        Report("PCIe link", d.TryGetPcieLink(out uint gen, out uint width, out uint maxGen, out uint maxWidth),
            $"Gen{gen} x{width} (max Gen{maxGen} x{maxWidth})");
        Report("PCIe TX", d.TryGetPcieThroughput(NvmlPcieUtilCounter.TxBytes, out uint tx), $"{tx / 1024.0:F1} MB/s");
        Report("PCIe RX", d.TryGetPcieThroughput(NvmlPcieUtilCounter.RxBytes, out uint rx), $"{rx / 1024.0:F1} MB/s");
        Report("PCI info", d.TryGetPciInfo(out _, out _, out _, out string busId), busId);

        Report("Core offset", d.TryGetClockOffset(NvmlClockType.Graphics, out var coreOff),
            $"{coreOff.ClockOffsetMHz} MHz (range {coreOff.MinClockOffsetMHz}..{coreOff.MaxClockOffsetMHz})");
        Report("Mem offset", d.TryGetClockOffset(NvmlClockType.Mem, out var memOff),
            $"{memOff.ClockOffsetMHz} MHz (range {memOff.MinClockOffsetMHz}..{memOff.MaxClockOffsetMHz})");

        Report("Throttle margin", d.TryGetThrottleMargin(out int margin), $"{margin} C headroom");

        for (uint f = 0; f < 3; f++)
        {
            Report($"Fan[{f}] RPM", d.TryGetFanRpm(f, out uint rpm), $"{rpm} RPM");
        }

        Report("Fan[0] target", d.TryGetTargetFanSpeed(0, out uint target), $"{target}%");

        var fields = new NvmlFieldValue[]
        {
            new() { FieldId = NvmlFieldValue.FiPowerInstant },
            new() { FieldId = NvmlFieldValue.FiTemperatureShutdownTLimit },
            new() { FieldId = NvmlFieldValue.FiTemperatureSlowdownTLimit },
            new() { FieldId = NvmlFieldValue.FiTemperatureGpuMaxTLimit },
            new() { FieldId = NvmlFieldValue.FiPcieCountTxBytes },
            new() { FieldId = NvmlFieldValue.FiPcieCountRxBytes },
        };
        var fieldsRc = d.TryGetFieldValues(fields);
        Report("Field values", fieldsRc, string.Empty);
        if (fieldsRc == NvmlReturn.Success)
        {
            string[] names = ["power instant (mW)", "shutdown TLimit (C)", "slowdown TLimit (C)", "gpumax TLimit (C)", "pcie tx bytes", "pcie rx bytes"];
            bool[] signed = [false, true, true, true, false, false];
            for (int i = 0; i < fields.Length; i++)
            {
                string value = fields[i].Status == NvmlReturn.Success
                    ? (signed[i]
                        ? unchecked((int)fields[i].Value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : fields[i].Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    : $"[{fields[i].Status}]";
                Console.WriteLine($"    {names[i],-24} {value}");
            }
        }
    }

    private static void Report(string label, NvmlReturn rc, string value)
    {
        string status = rc == NvmlReturn.Success ? "OK  " : $"[{rc}]";
        Console.WriteLine($"  {label,-26} {status,-24} {(rc == NvmlReturn.Success ? value : string.Empty)}");
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
