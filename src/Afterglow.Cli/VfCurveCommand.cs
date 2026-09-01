using System.Globalization;
using Afterglow.Core.Hardware;
using Afterglow.Core.Tuning;

namespace Afterglow.Cli;

/// <summary>
/// `vfcurve [--seconds N] [--load] [--json]` — records and prints the measured
/// voltage/frequency curve. With --load, drives the GPU through a range of
/// intensities so the curve fills in across voltages instead of only where the
/// desktop happens to sit.
/// </summary>
internal static class VfCurveCommand
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOut = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        int seconds = 40;
        bool drive = args.Contains("--load");
        bool probe = args.Contains("--probe");
        bool json = args.Contains("--json");
        bool fresh = args.Contains("--fresh");

        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--seconds" &&
                int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
            {
                seconds = Math.Clamp(s, 5, 900);
            }
        }

        using var manager = new GpuManager();
        if (manager.Gpus.Count == 0)
        {
            Console.Error.WriteLine($"No supported GPU found (NVML: {manager.NvmlStatus}, IGCL: {manager.IgclStatus}).");
            return 1;
        }

        uint gpuIndex = CliGpu.ParseIndex(args) ?? manager.Gpus[0].Index;
        var gpu = manager.Gpus.FirstOrDefault(g => g.Index == gpuIndex);
        if (gpu is null)
        {
            Console.Error.WriteLine($"GPU {gpuIndex} not found — {manager.Gpus.Count} GPU(s) detected.");
            return 2;
        }

        var recorder = new VfCurveRecorder
        {
            DeviceIndex = gpu.Index,
            PersistPath = VfCurveRecorder.PathFor(gpu.Uuid, isPrimary: gpu.Index == manager.Gpus[0].Index),
        };
        if (!fresh)
        {
            recorder.Load();
        }

        if (probe)
        {
            // Active sweep: lock the clock at each step under load and record the
            // voltage the driver selects — the definitive way to map the curve.
            if (!json)
            {
                Console.WriteLine("Probing the V/F curve: locking each clock step under load (requires administrator)…");
            }

            var vfProbe = new VfCurveProbe(gpu.Tuner, () => gpu.Poller.Poll())
            {
                TargetPciBusId = gpu.PciBusId,
                TargetVendorId = gpu.PciVendorId,
            };
            bool refused = false;
            vfProbe.ProgressChanged += progress =>
            {
                if (progress.Phase.Contains("refused", StringComparison.OrdinalIgnoreCase))
                {
                    refused = true;
                }

                if (!json && progress.Running && progress.MeasuredVoltageMv is double mv)
                {
                    Console.WriteLine(
                        $"  step {progress.StepIndex,2}/{progress.StepCount}: lock {progress.TargetClockMHz,5} MHz -> " +
                        $"{progress.MeasuredClockMHz,7:F0} MHz @ {mv,7:F1} mV");
                }
            };
            vfProbe.Run(recorder);
            if (refused)
            {
                Console.Error.WriteLine("Clock locking was refused — run elevated (administrator).");
                return 1;
            }
        }
        else
        {
            if (!json)
            {
                Console.WriteLine($"Recording the V/F curve for {seconds} s{(drive ? " while driving load" : string.Empty)}…");
            }

            Stress.GpuStressTestRunner? runner = null;
            try
            {
                if (drive)
                {
                    runner = new Stress.GpuStressTestRunner { TargetPciBusId = gpu.PciBusId, TargetVendorId = gpu.PciVendorId };
                    runner.Start();
                }

                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                int step = 0;
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(250);
                    recorder.Add(gpu.Poller.Poll());

                    // Sweep intensity so the GPU visits several voltage/clock states.
                    if (drive && ++step % 24 == 0)
                    {
                        runner!.NextIntensity();
                    }
                }
            }
            finally
            {
                runner?.Dispose();
            }
        }

        recorder.Save();
        var curve = recorder.GetCurve();

        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                gpu = gpu.Name,
                samples = recorder.TotalSamples,
                points = curve,
            }, JsonOut));
            return curve.Count > 0 ? 0 : 3;
        }

        Console.WriteLine($"{gpu.Name}: {curve.Count} voltage points from {recorder.TotalSamples:N0} samples under load");
        foreach (var bin in curve)
        {
            Console.WriteLine(
                $"  {bin.VoltageMv,7:F0} mV -> max {bin.MaxClockMHz,7:F0} MHz  (avg {bin.AvgClockMHz,7:F0}, {bin.Samples,5} samples)");
        }

        return curve.Count > 0 ? 0 : 3;
    }
}
