using System.Globalization;
using Afterglow.Core.Metrics;

namespace Afterglow.Cli;

/// <summary>`fps [--seconds N]` — capture present events and report per-app frame statistics.</summary>
internal static class FpsCommand
{
    public static int Run(string[] args)
    {
        int seconds = 15;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--seconds" &&
                int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
            {
                seconds = Math.Clamp(s, 3, 600);
            }
        }

        using var service = new FrameMetricsService(TimeSpan.FromSeconds(Math.Max(seconds, 10)));
        Console.WriteLine($"Starting ETW present capture for {seconds} s (needs elevation)...");
        if (!service.Start())
        {
            Console.Error.WriteLine(service.Session.FailureReason ?? "Capture could not start.");
            return 1;
        }

        for (int elapsed = 0; elapsed < seconds; elapsed++)
        {
            Thread.Sleep(1000);
            if (service.Session.State == PresentMonState.Failed)
            {
                Console.Error.WriteLine(service.Session.FailureReason);
                return 1;
            }
        }

        var apps = service.GetTrackedApps();
        Console.WriteLine(
            $"Capture state: {service.Session.State}, stdout lines: {service.Session.TotalLines}, " +
            $"parse errors: {service.Session.ParseErrors}, header parsed: {service.Session.HeaderParsed}");
        if (!service.Session.HeaderParsed)
        {
            foreach (string line in service.Session.FirstLines)
            {
                Console.WriteLine($"  raw: {line}");
            }
        }
        Console.WriteLine($"Presenting apps seen: {apps.Count}");
        foreach (var app in apps.Take(10))
        {
            var stats = service.GetStats(app.ProcessId);
            if (stats is null)
            {
                Console.WriteLine($"  {app.Application,-34} pid {app.ProcessId,-7} {app.RecentFrames} frames (not enough for stats)");
                continue;
            }

            var s = stats.Value.Stats;
            Console.WriteLine(
                $"  {app.Application,-34} pid {app.ProcessId,-7} {s.AverageFps,7:F1} fps  " +
                $"P1 {s.P1Fps,6:F1}  1%low {s.Low1Fps,6:F1}  ft {s.AverageFrametimeMs,6:F2} ms  [{app.PresentMode}]");
        }

        var target = service.GetTargetStats();
        if (target is not null)
        {
            Console.WriteLine($"Auto-selected target: {target.Value.App.Application}");
        }

        int exitCode = apps.Count > 0 ? 0 : 3;
        service.Dispose();

        // A killed elevated child can leave a lingering handle that blocks normal
        // process exit; the CLI has printed everything it needs to.
        Environment.Exit(exitCode);
        return exitCode;
    }
}
