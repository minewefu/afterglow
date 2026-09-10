using Afterglow.Core.Stress;

namespace Afterglow.Cli;

/// <summary>`vram [--seconds N]` — full-capacity VRAM test with GPU-side verification.</summary>
internal static class VramCommand
{
    public static int Run(string[] args)
    {
        int seconds = 120;
        if (CliArgs.Validate(args, "vram") is string argError)
        {
            Console.Error.WriteLine(argError);
            return 2;
        }

        if (CliArgs.TryInt(args, "--seconds", 15, 86_400, ref seconds) is string secondsError)
        {
            Console.Error.WriteLine(secondsError);
            return 2;
        }

        var (bus, vendorId, busError) = CliGpu.ResolveTarget(args);
        if (busError is not null)
        {
            Console.Error.WriteLine(busError);
            return 1;
        }

        using var vram = new VramTest
        {
            TargetPciBusId = bus,
            TargetVendorId = vendorId,

            // Same rule as `stress`: only a deliberately unbound run keeps the
            // historical fallback.
            AllowUnboundGuess = bus is null,
        };
        var done = new ManualResetEventSlim(false);
        VramProgress? final = null;

        vram.ProgressChanged += progress =>
        {
            if (progress.State == StressState.Running)
            {
                Console.Write(
                    $"\r  {progress.Elapsed:hh\\:mm\\:ss}  {progress.PlannedBytes / (double)(1L << 30),5:F1} GiB planned  " +
                    $"round {progress.Rounds + 1}  {progress.GiBPerSecond,6:F1} GiB/s verified  errors: {progress.ErrorCount}   ");
            }
            else
            {
                final = progress;
                done.Set();
            }
        };

        Console.WriteLine(
            $"VRAM test: fill + verify as much of the card as the OS will safely give out, " +
            $"for {seconds} s (at least one full round). Ctrl+C aborts.");
        bool aborted = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            aborted = true;
            vram.Stop();
        };

        vram.Start();

        // Run for the window, but always complete at least one full round.
        var start = DateTime.UtcNow;
        bool stoppedCleanly = true;
        while (!done.IsSet)
        {
            if (done.Wait(TimeSpan.FromMilliseconds(500)))
            {
                break;
            }

            var p = vram.Progress;
            double elapsed = (DateTime.UtcNow - start).TotalSeconds;
            if ((elapsed >= seconds && p.Rounds >= 1) || elapsed >= seconds * 3)
            {
                stoppedCleanly = vram.StopAndWait(TimeSpan.FromSeconds(30));
                break;
            }
        }

        final ??= vram.Progress;
        Console.WriteLine();
        Console.WriteLine(
            $"Result: {final.State} after {final.Elapsed:hh\\:mm\\:ss} — " +
            $"{final.PlannedBytes / (double)(1L << 30):F1} GiB covered × {final.Rounds} full rounds, " +
            $"{final.ErrorCount} errors.");
        if (final.Detail is { } detail && final.State != StressState.Running)
        {
            Console.WriteLine($"  {detail}");
        }

        // An abandoned run is not a pass: the figures above are a stale mid-run
        // snapshot and nothing was verified after them.
        if (!stoppedCleanly)
        {
            Console.Error.WriteLine(
                "  The VRAM test did not stop within 30 s — the figures above are a stale mid-run " +
                "snapshot, not a completed run.");
            return 1;
        }

        if (aborted)
        {
            Console.Error.WriteLine(
                "  Aborted before the requested window elapsed — this is not a pass. " +
                "The figures above cover only the part that ran.");
            return 1;
        }

        bool passed = final.State is StressState.Stopped && final.Rounds >= 1;
        return passed ? 0 : 1;
    }
}
