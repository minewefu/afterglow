using System.Globalization;
using Afterglow.Core.Stress;

namespace Afterglow.Cli;

/// <summary>`vram [--seconds N]` — full-capacity VRAM test with GPU-side verification.</summary>
internal static class VramCommand
{
    public static int Run(string[] args)
    {
        int seconds = 120;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--seconds" &&
                int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
            {
                seconds = Math.Clamp(s, 15, 86_400);
            }
        }

        var (bus, busError) = CliGpu.ResolveBus(args);
        if (busError is not null)
        {
            Console.Error.WriteLine(busError);
            return 1;
        }

        using var vram = new VramTest { TargetPciBusId = bus };
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
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            vram.Stop();
        };

        vram.Start();

        // Run for the window, but always complete at least one full round.
        var start = DateTime.UtcNow;
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
                vram.StopAndWait(TimeSpan.FromSeconds(10));
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

        bool passed = final.State is StressState.Stopped or StressState.Running && final.Rounds >= 1;
        return passed ? 0 : 1;
    }
}
