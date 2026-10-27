using System.Globalization;
using Afterglow.Cli;
using Afterglow.Core.Metrics;

namespace Afterglow.Core.Tests;

/// <summary>
/// The capture pipeline from PresentMon's CSV lines to the numbers the overlay,
/// the dashboard and the CLI report. Lines go in through the session's parser
/// exactly as PresentMon prints them; no PresentMon process is started (a start
/// against a path that does not exist stands in for launching one).
/// </summary>
public class FrameCaptureTests
{
    private const string Header = "Application,ProcessID,SwapChainAddress,PresentMode,TimeInSeconds,MsBetweenPresents";

    private static readonly string NoPresentMon =
        Path.Combine(Path.GetTempPath(), $"afterglow-no-presentmon-{Guid.NewGuid():N}.exe");

    /// <summary>Frames at a steady rate, timestamped (PresentMon timeline) from just after <paramref name="fromS"/> to <paramref name="toS"/>.</summary>
    private static void Present(
        PresentMonSession session, string app, int pid, string chain, double fps, double fromS, double toS)
    {
        double frametime = 1000.0 / fps;
        int frames = (int)Math.Round((toS - fromS) * fps);
        for (int i = 1; i <= frames; i++)
        {
            double t = (fromS * 1000.0) + (i * frametime);
            session.ProcessLine(string.Create(CultureInfo.InvariantCulture,
                $"{app},{pid},{chain},Hardware: Independent Flip,{t / 1000.0:F6},{frametime:F6}"));
        }
    }

    /// <summary>
    /// R-26. The GUI's Stop ends PresentMon but kept the service's windows, and a
    /// new PresentMon's timestamps restart at zero — so the old frames never aged
    /// out and the next capture reported a blend of both runs.
    /// </summary>
    [Fact]
    public void A_new_capture_starts_without_the_previous_captures_frames()
    {
        using var service = new FrameMetricsService(TimeSpan.FromSeconds(30));
        service.Session.ProcessLine(Header);
        Present(service.Session, "game.exe", 4242, "0xA", fps: 100, fromS: 0, toS: 30);   // 3000 frames
        Assert.Equal(3000, service.GetStats(4242)!.Value.Stats.FrameCount);

        // Stop, then start again: the restart is what must discard capture state.
        service.Session.Dispose();
        Assert.False(service.Start(NoPresentMon));

        // The new PresentMon prints its header again and restarts its clock at 0.
        service.Session.ProcessLine(Header);
        Present(service.Session, "game.exe", 4242, "0xA", fps: 50, fromS: 0, toS: 30);    // 1500 frames

        var stats = service.GetStats(4242)!.Value.Stats;
        Assert.Equal(1500, stats.FrameCount);       // was 4500
        Assert.Equal(50, stats.AverageFps, 3);       // was 75
    }

    [Fact]
    public void Stopping_a_capture_drops_its_windows()
    {
        using var service = new FrameMetricsService(TimeSpan.FromSeconds(30));
        service.Session.ProcessLine(Header);
        Present(service.Session, "game.exe", 4242, "0xA", fps: 100, fromS: 0, toS: 5);
        Assert.Single(service.GetCapturedApps());

        service.Stop();

        Assert.Empty(service.GetCapturedApps());
        Assert.Null(service.GetStats(4242, requireFresh: false));
    }

    [Fact]
    public void Afterglows_own_window_is_never_a_capture_target()
    {
        // As the foreground window it used to become "the game" the moment the
        // user switched to it — including to press Stop after the game exited.
        using var service = new FrameMetricsService(TimeSpan.FromSeconds(30));
        service.Session.ProcessLine(Header);
        Present(service.Session, "Afterglow.exe", Environment.ProcessId, "0xF", fps: 240, fromS: 0, toS: 5);
        Present(service.Session, "game.exe", 4242, "0xA", fps: 60, fromS: 0, toS: 5);
        service.ForegroundProcessId = Environment.ProcessId;

        Assert.Equal(4242, service.ResolveTargetPid());
        Assert.DoesNotContain(service.GetCapturedApps(), app => app.ProcessId == Environment.ProcessId);
    }

    /// <summary>
    /// R-27. A chain is evicted only when it receives a frame, so a swap chain the
    /// game stopped using (a resolution change, a fullscreen toggle) kept its whole
    /// window and out-counted the chain the game presents through now — forever,
    /// when it had run faster than the new one ever could within a window.
    /// </summary>
    [Fact]
    public void A_swap_chain_that_stopped_presenting_cannot_win_selection()
    {
        using var service = new FrameMetricsService(TimeSpan.FromSeconds(30));
        service.Session.ProcessLine(Header);
        Present(service.Session, "game.exe", 4242, "0xA", fps: 240, fromS: 0, toS: 30);   // 7200 frames, then silent

        // Ten seconds on the new chain: the old one still holds 4800 frames of the window.
        Present(service.Session, "game.exe", 4242, "0xB", fps: 60, fromS: 30, toS: 40);
        var early = service.GetStats(4242)!.Value;
        Assert.Equal(60, early.Stats.AverageFps, 3);
        Assert.All(service.GetTargetFrametimes(50), ft => Assert.Equal(1000.0 / 60, ft, 3));

        // A minute on the new chain: a 30 s window of it can never reach 7200 frames.
        Present(service.Session, "game.exe", 4242, "0xB", fps: 60, fromS: 40, toS: 90);
        var late = service.GetStats(4242)!.Value;
        Assert.Equal(60, late.Stats.AverageFps, 3);   // was 240
        Assert.InRange(late.Stats.FrameCount, 1800, 1801);   // 30 s at 60 FPS, both edge frames inclusive; was 7200
    }

    /// <summary>
    /// R-30. The end-of-run summary listed only applications that had presented
    /// in the last ten seconds, so a benchmark that finished fifteen seconds
    /// before the capture did vanished while a background browser stayed.
    /// </summary>
    [Fact]
    public void The_cli_summary_lists_an_application_that_finished_before_the_capture_did()
    {
        using var service = new FrameMetricsService(TimeSpan.FromSeconds(60));
        service.Session.ProcessLine(Header);
        Present(service.Session, "benchmark.exe", 100, "0x1", fps: 144, fromS: 0, toS: 45);
        Present(service.Session, "browser.exe", 200, "0x2", fps: 60, fromS: 0, toS: 60);

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode = FpsCommand.WriteSummary(service, output);

        string summary = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("Presenting apps seen: 2", summary, StringComparison.Ordinal);
        Assert.Contains("benchmark.exe", summary, StringComparison.Ordinal);
        Assert.Contains("144.0 fps", summary, StringComparison.Ordinal);
        Assert.Contains("browser.exe", summary, StringComparison.Ordinal);
    }
}
