using System.Text;
using Afterglow.Core;
using Afterglow.Core.Fans;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Services;
using Afterglow.Core.Telemetry;

namespace Afterglow.Core.Tests;

/// <summary>
/// R-05: app shutdown runs as independent steps, a failing log cannot skip the
/// fan release, and only a hardware release that really happened lets the
/// session be stamped clean. The App's teardown (AppServices.Shutdown,
/// App.OnExit) is built from these pieces in this order.
/// </summary>
[Collection("AppPaths")]
public sealed class ShutdownCleanupTests : IDisposable
{
    private const string Uuid = "GPU-5d0c1ea2-0000-4000-8000-0000000000a5";

    private readonly Fakes.StoreScope _store = new();

    public void Dispose() => _store.Dispose();

    private static CsvLogger LoggerOn(Fakes.DiskFullStream disk) =>
        new(Path.Combine(AppPaths.Root, "session.csv"), _ => new StreamWriter(disk, Encoding.UTF8));

    [Fact]
    public void A_failed_csv_flush_still_closes_the_log_and_stopping_again_is_quiet()
    {
        var disk = new Fakes.DiskFullStream();
        using var logger = LoggerOn(disk);
        logger.Start();

        // The lost tail is still reported to whoever stopped the log...
        Assert.Throws<IOException>(logger.Stop);

        // ...but the logger is stopped and its file closed, so the next Stop
        // (the toggle, then app shutdown) does not throw the same error again.
        Assert.False(logger.IsRunning);
        Assert.True(disk.Closed);
        logger.Stop();
    }

    [Fact]
    public void A_csv_log_on_a_full_disk_does_not_skip_the_fan_release()
    {
        // The reported sequence: the CSV logger's flush threw IOException out of
        // the teardown before the fans were released, leaving them at 40 %.
        var disk = new Fakes.DiskFullStream();
        using var logger = LoggerOn(disk);
        logger.Start();
        int releases = 0;
        using var fans = new FanControlService(_ => NvmlReturn.Success, () =>
        {
            releases++;
            return NvmlReturn.Success;
        }, Uuid, 30);
        Assert.True(fans.SetFixed(40));

        var steps = new ShutdownSteps();
        steps.Run("CSV log", logger.Dispose);
        steps.RunEssential("fan release", fans.ReleaseForShutdown);

        Assert.Equal(1, releases);
        Assert.Equal(FanControlMode.Auto, fans.Mode);

        // A log the disk refused left no hardware changed: still a clean exit.
        Assert.True(steps.EssentialSucceeded);
    }

    [Fact]
    public void Every_step_runs_even_after_one_throws()
    {
        var ran = new List<string>();
        var steps = new ShutdownSteps();

        steps.Run("first", () => ran.Add("first"));
        steps.Run("throws", () => throw new IOException("disk gone"));
        steps.RunEssential("throws essential", () => throw new InvalidOperationException("driver gone"));
        steps.Run("last", () => ran.Add("last"));

        Assert.Equal(["first", "last"], ran);
        Assert.Equal(["throws", "throws essential"], steps.Failed);
    }

    [Fact]
    public void Only_an_essential_step_that_failed_or_threw_withholds_the_clean_mark()
    {
        var cleanupOnly = new ShutdownSteps();
        cleanupOnly.Run("log", () => throw new IOException("disk full"));
        cleanupOnly.RunEssential("fans", () => true);
        Assert.True(cleanupOnly.EssentialSucceeded);

        var refused = new ShutdownSteps();
        refused.RunEssential("fans", () => false);
        refused.RunEssential("other card", () => true);   // a later success does not undo it
        Assert.False(refused.EssentialSucceeded);

        var threw = new ShutdownSteps();
        threw.RunEssential("fans", () => throw new InvalidOperationException("driver gone"));
        Assert.False(threw.EssentialSucceeded);
    }
}
