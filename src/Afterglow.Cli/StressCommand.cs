using Afterglow.Core.Stress;

namespace Afterglow.Cli;

/// <summary>`stress [--seconds N] [--intensity N]` — burn test with bit-exact error checking.</summary>
internal static class StressCommand
{
    public static int Run(string[] args)
    {
        int seconds = 30;
        uint intensity = 4096;
        var pattern = StressPattern.Sustained;

        // Nothing here may be silently ignored. A mistyped --pattern used to
        // fall through to the sustained burn and still report a pass, so the
        // regime the user picked for catching marginal memory was quietly
        // replaced by one that cannot catch it — a stability verdict for work
        // they never asked for.
        if (CliArgs.Validate(args, "stress") is string argError)
        {
            Console.Error.WriteLine(argError);
            return 2;
        }

        if (CliArgs.TryInt(args, "--seconds", 5, 86_400, ref seconds) is string secondsError)
        {
            Console.Error.WriteLine(secondsError);
            return 2;
        }

        if (CliArgs.TryUInt(args, "--intensity", 128, 16_384, ref intensity) is string intensityError)
        {
            Console.Error.WriteLine(intensityError);
            return 2;
        }

        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] != "--pattern")
            {
                continue;
            }

            switch (args[i + 1].ToUpperInvariant())
            {
                case "SUSTAINED":
                    pattern = StressPattern.Sustained;
                    break;
                case "TRANSITIONS" or "TRANSITION":
                    pattern = StressPattern.Transitions;
                    break;
                case "EXCURSIONS" or "EXCURSION" or "BURSTS" or "DWELL":
                    pattern = StressPattern.BoostExcursions;
                    break;
                default:
                    Console.Error.WriteLine(
                        $"Unknown --pattern '{args[i + 1]}' (expected sustained, transitions or excursions).");
                    return 2;
            }
        }

        // A transitions run shorter than its first load/idle cycle counts no
        // excursion and so can never earn a verdict; refuse it up front rather
        // than burn and then print "inconclusive — give it more seconds".
        if (GpuStressTest.SecondsShortfall(pattern, seconds) is string tooShort)
        {
            Console.Error.WriteLine($"'--seconds {seconds}': {tooShort}.");
            return 2;
        }

        // Hidden diagnostic: show how each NVML GPU resolves to a D3D adapter
        // (exercises the LUID→PCI-bus binding without burning anything).
        if (args.Contains("--probe-adapter"))
        {
            return ProbeAdapter();
        }

        var (bus, vendorId, busError) = CliGpu.ResolveTarget(args);
        if (busError is not null)
        {
            Console.Error.WriteLine(busError);
            return 1;
        }

        using var stress = new GpuStressTest
        {
            IterationsPerDispatch = intensity,
            Pattern = pattern,
            TargetPciBusId = bus,
            TargetVendorId = vendorId,

            // `stress` with no --gpu is explicitly an exploratory run, and the
            // historical largest-VRAM fallback for that case is documented
            // behaviour the release preserves. Everything that attributes a
            // result to a named card leaves this false and gets the refusal.
            AllowUnboundGuess = bus is null,
        };
        var done = new ManualResetEventSlim(false);
        StressProgress? final = null;

        stress.ProgressChanged += progress =>
        {
            if (progress.State == StressState.Running)
            {
                string phase = progress.Phase is { } p
                    ? $"[{p}] transitions: {progress.Transitions}  "
                    : string.Empty;
                Console.Write($"\r  {progress.Elapsed:hh\\:mm\\:ss}  {phase}{progress.DispatchesPerSecond,7:F1} dispatches/s  " +
                              $"{progress.TotalDispatches,8} total  errors: {progress.ErrorCount}   ");
            }
            else
            {
                final = progress;
                done.Set();
            }
        };

        Console.WriteLine(
            $"Burn test: {seconds} s at intensity {intensity}, pattern {pattern} " +
            "(bit-exact verification every ~2 s). Ctrl+C aborts.");
        bool aborted = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            aborted = true;
            stress.Stop();
        };

        stress.Start();
        bool stoppedCleanly = true;
        if (!done.Wait(TimeSpan.FromSeconds(seconds)))
        {
            stoppedCleanly = stress.StopAndWait(TimeSpan.FromSeconds(30));
            final ??= stress.Progress;
        }

        Console.WriteLine();
        string transitionsNote = final!.Transitions > 0
            ? $", {final.Transitions} clock transitions verified"
            : string.Empty;
        Console.WriteLine($"Result: {final.State} after {final.Elapsed:hh\\:mm\\:ss}, " +
                          $"{final.TotalDispatches} dispatches, {final.ErrorCount} errors{transitionsNote}.");
        if (final.Detail is { } detail)
        {
            Console.WriteLine($"  {detail}");
        }

        // An abandoned burn is not a pass. The figures above came from the last
        // progress report before the worker stopped answering, and nothing was
        // verified after it — say so and fail, rather than exiting 0 on a run
        // that never finished.
        if (!stoppedCleanly)
        {
            Console.Error.WriteLine(
                "  The burn did not stop within 30 s — the figures above are a stale mid-run snapshot, " +
                "not a completed run, and no stability conclusion can be drawn from them.");
            return 1;
        }

        // A detected artifact, TDR or engine failure is a RESULT, already
        // printed above with its real cause. Falling through to the zero-work
        // branch appended "setup outlasted the requested window — give it more
        // seconds" underneath a driver reset, contradicting the true diagnosis
        // one line up and advising exactly the wrong thing.
        if (final.State is StressState.ArtifactDetected or StressState.DeviceLost or StressState.Failed)
        {
            return 1;
        }

        // Neither is a run that never entered its load loop, or a cycling
        // pattern that completed no cycle. The dispatch count above includes a
        // one-off reference pass, so it reads 1 even then — and the closing
        // verification would have compared the reference buffer against itself
        // and matched by construction. This exit code is documented to
        // automation as "0 = stable" (docs/agent-integration.md), so it must not
        // be 0 for a run that proved nothing; the rule is the engine's own.
        if (final.VerdictGap is { } gap)
        {
            Console.Error.WriteLine(
                $"  {char.ToUpperInvariant(gap[0])}{gap[1..]} — this is not a pass. " +
                "Give it more seconds, or a lower --intensity.");
            return 1;
        }

        if (aborted)
        {
            // The burn stopped because the user stopped it, not because it
            // survived the window. Exiting 0 told every scripted consumer
            // "stable" for a test that never finished.
            Console.Error.WriteLine(
                "  Aborted before the requested window elapsed — this is not a pass. " +
                "The counters above cover only the part that ran.");
            return 1;
        }

        return final.IsCleanPass ? 0 : 1;
    }

    private static int ProbeAdapter()
    {
        using var manager = new Afterglow.Core.Hardware.GpuManager();
        if (manager.Gpus.Count == 0)
        {
            Console.WriteLine($"No supported GPU found (NVML: {manager.NvmlStatus}, IGCL: {manager.IgclStatus}).");
        }

        foreach (var gpu in manager.Gpus)
        {
            string source = gpu.PciVendorId == StressAdapter.NvidiaVendorId ? "NVML" : "IGCL";
            Console.WriteLine(FormattableString.Invariant(
                $"GPU {gpu.Index}: {gpu.Name}  ({source} PCI bus {(gpu.PciBusId is { } b ? b : (object)"?")}, UUID {gpu.Uuid ?? "?"})"));
            using var bound = StressAdapter.Select(gpu.PciVendorId, gpu.PciBusId, out string boundDesc);
            Console.WriteLine($"  bus-bound D3D adapter: {(bound is null ? "FAILED" : "ok")} — {boundDesc}");
        }

        // Probe the same vendor an actual unbound run would resolve, so the
        // diagnostic describes what the engine will really do.
        uint fallbackVendor = StressAdapter.DetectDefaultVendor();
        string fallbackLabel = fallbackVendor == StressAdapter.NvidiaVendorId ? "largest NVIDIA" : "largest Intel";
        using var fallback = StressAdapter.Select(fallbackVendor, null, out string fallbackDesc);
        Console.WriteLine($"no-bus fallback ({fallbackLabel}): {(fallback is null ? "none" : fallbackDesc)}");
        return 0;
    }
}
