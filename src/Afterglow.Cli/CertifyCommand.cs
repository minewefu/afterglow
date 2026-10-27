using Afterglow.Core.Fans;
using Afterglow.Core.Hardware;
using Afterglow.Core.Profiles;

namespace Afterglow.Cli;

/// <summary>
/// `certify --profile NAME [--seconds N] [--gpu N]` — applies a saved profile
/// and runs all four stability modes against it (sustained, transitions,
/// excursions, VRAM), stamping each pass into the profile. All four = marked
/// stable.
/// </summary>
internal static class CertifyCommand
{
    public static int Run(string[] args)
    {
        string? name = null;
        int seconds = 90;

        // Certification stamps a profile as stable. A run that quietly differed
        // from the one asked for — a mistyped duration falling back to the
        // 90-second default — would put that stamp on work the user never chose.
        if (CliArgs.Validate(args, "certify") is string argError)
        {
            Console.Error.WriteLine(argError);
            return 2;
        }

        if (CliArgs.TryInt(args, "--seconds", 30, 1800, ref seconds) is string secondsError)
        {
            Console.Error.WriteLine(secondsError);
            return 2;
        }

        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--profile")
            {
                name = args[i + 1];
            }
        }

        if (name is null)
        {
            Console.Error.WriteLine("Usage: afterglow-cli certify --profile NAME [--seconds N]");
            return 2;
        }

        var store = new ProfileStore();
        var profile = store.Load(name);
        if (profile is null)
        {
            Console.Error.WriteLine(
                $"Profile '{name}' not found. Saved profiles: " +
                string.Join(", ", store.LoadAll().Select(p => $"'{p.Name}'")));
            return 2;
        }

        if (FanPolicyRefusal(profile) is string fanRefusal)
        {
            Console.Error.WriteLine(fanRefusal);
            return 2;
        }

        using var manager = new GpuManager();
        if (manager.Gpus.Count == 0)
        {
            Console.Error.WriteLine($"No supported GPU available (NVML: {manager.NvmlStatus}, IGCL: {manager.IgclStatus}).");
            return 1;
        }

        if (!CliGpu.TryIndexOrFirst(args, manager.Gpus[0].Index, out uint gpuIndex, out string? gpuArgError))
        {
            Console.Error.WriteLine(gpuArgError);
            return 2;
        }

        var gpu = manager.Gpus.FirstOrDefault(g => g.Index == gpuIndex);
        if (gpu is null)
        {
            Console.Error.WriteLine($"GPU {gpuIndex} not found — {manager.Gpus.Count} GPU(s) detected.");
            return 2;
        }

        // The profile's fan policy is part of what gets certified (R-11), so the
        // run goes through the card's fan control. This process cannot tell what
        // the fans were doing before it started — no telemetry, no saved fan
        // settings of its own — so afterwards they are handed back to firmware
        // control, and the log says that is what happened.
        using var fanService = new FanControlService(gpu.Tuner);
        var certifier = new ProfileCertifier(gpu.Tuner, store, gpu.PciBusId, gpu.PciVendorId,
            new CertificationFans(fanService, () => null));
        var done = new ManualResetEventSlim(false);
        int lastLogCount = 0;
        CertifierStatus? final = null;

        certifier.StatusChanged += status =>
        {
            lock (Console.Out)
            {
                for (; lastLogCount < status.Log.Count; lastLogCount++)
                {
                    Console.WriteLine();
                    Console.WriteLine(status.Log[lastLogCount]);
                }

                if (status.Running && status.ModeDuration > TimeSpan.Zero)
                {
                    Console.Write(
                        $"\r  [{status.ModeIndex + 1}/{status.ModeCount}] {status.Phase}  " +
                        $"{status.ModeElapsed:mm\\:ss} / {status.ModeDuration:mm\\:ss}   ");
                }
            }

            if (!status.Running)
            {
                final = status;
                done.Set();
            }
        };

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            certifier.Cancel();
        };

        certifier.Start(profile, new CertifierOptions { SecondsPerMode = seconds });
        done.Wait();
        Console.WriteLine();

        return final?.Passed == true ? 0 : 1;
    }

    /// <summary>
    /// Why this command cannot certify the profile as saved, or null. A fan
    /// curve is software: it moves the fans only while something feeds it
    /// temperatures, which the app's telemetry does and this one-shot command
    /// does not. Burning a curve profile here would test its clocks under
    /// whatever the fans happened to be doing — the different thermal
    /// configuration certification must not stamp (R-11).
    /// </summary>
    internal static string? FanPolicyRefusal(TuningProfile profile) =>
        profile.FanMode == FanMode.Curve
            ? $"Profile '{profile.Name}' drives its fans with a software fan curve, which runs only while the " +
              "Afterglow app is open. Certify it from the app's Profiles page; certifying it here would burn its " +
              "clocks under a different fan configuration."
            : null;
}
