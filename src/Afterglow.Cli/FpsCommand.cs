using Afterglow.Core.Metrics;

namespace Afterglow.Cli;

/// <summary>`fps [--seconds N]` — capture present events and report per-app frame statistics.</summary>
internal static class FpsCommand
{
    public static int Run(string[] args)
    {
        int seconds = 15;
        if (CliArgs.Validate(args, "fps") is string argError)
        {
            Console.Error.WriteLine(argError);
            return 2;
        }

        if (CliArgs.TryInt(args, "--seconds", 3, 600, ref seconds) is string secondsError)
        {
            Console.Error.WriteLine(secondsError);
            return 2;
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
            // A report over a finished capture, not a live readout: a game that
            // quit a few seconds before the window closed still has its whole
            // frame window retained, and the freshness gate that blanks the
            // overlay used to withhold it here — thousands of captured frames
            // printed with no statistics. The staleness is labelled instead.
            var stats = service.GetStats(app.ProcessId, requireFresh: false);
            if (stats is null)
            {
                Console.WriteLine(
                    $"  {app.Application,-34} pid {app.ProcessId,-7} {app.RecentFrames} frames (not enough frames for stats)");
                continue;
            }

            var s = stats.Value.Stats;
            string liveness = service.IsLive(app.ProcessId)
                ? string.Empty
                : "  (stopped presenting before the capture ended)";
            Console.WriteLine(
                $"  {app.Application,-34} pid {app.ProcessId,-7} {s.AverageFps,7:F1} fps  " +
                $"P1 {s.P1Fps,6:F1}  1%low {s.Low1Fps,6:F1}  ft {s.AverageFrametimeMs,6:F2} ms  [{app.PresentMode}]{liveness}");
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
