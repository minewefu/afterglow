using System.Globalization;
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
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--seconds" &&
                int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
            {
                seconds = Math.Clamp(s, 5, 86_400);
            }

            if (args[i] == "--intensity" &&
                uint.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint n))
            {
                intensity = Math.Clamp(n, 128, 16_384);
            }

            if (args[i] == "--pattern")
            {
                pattern = args[i + 1].ToUpperInvariant() switch
                {
                    "TRANSITIONS" or "TRANSITION" => StressPattern.Transitions,
                    "EXCURSIONS" or "EXCURSION" or "BURSTS" or "DWELL" => StressPattern.BoostExcursions,
                    _ => StressPattern.Sustained,
                };
            }
        }

        using var stress = new GpuStressTest { IterationsPerDispatch = intensity, Pattern = pattern };
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
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stress.Stop();
        };

        stress.Start();
        if (!done.Wait(TimeSpan.FromSeconds(seconds)))
        {
            stress.StopAndWait(TimeSpan.FromSeconds(10));
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

        return final.State is StressState.Stopped or StressState.Running ? 0 : 1;
    }
}
