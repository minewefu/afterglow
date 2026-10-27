using System.Text;
using Afterglow.Core;
using Afterglow.Core.Fans;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Services;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// R-05: app shutdown runs as independent steps, a failing log cannot skip the
/// fan release, and only a hardware release that really happened lets the
/// session be stamped clean. The App's teardown (AppServices.Shutdown,
/// App.OnExit) is built from these pieces in this order. A driver that stops
/// answering cannot hold the sequence: R-07's wait on the fan driver is bounded.
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
    public async Task A_fan_driver_that_never_answers_still_lets_exit_finish_without_the_clean_mark()
    {
        // As OnExit composes it: the services' teardown (each card's fan
        // release among its steps), the clean mark only when every release
        // landed, then the mutexes. A release stuck in the driver used to stop
        // the sequence there for good: the mutexes stayed held, the process
        // never ended, and a relaunch only signalled the hung instance.
        var ct = TestContext.Current.CancellationToken;
        using var hold = new ManualResetEventSlim(false);
        using var fans = new FanControlService(_ => NvmlReturn.Success, () =>
        {
            hold.Wait(TimeSpan.FromSeconds(30));
            return NvmlReturn.Success;
        }, Uuid, 30);
        Assert.True(fans.SetFixed(40));
        AppliedStateStore.MarkCleanShutdown();   // only the release's outcome may change it now

        bool markedClean = false;
        bool mutexesReleased = false;
        try
        {
            var exit = Task.Run(() =>
            {
                var steps = new ShutdownSteps();
                steps.RunEssential("GPU 0 fan release", () => fans.ReleaseForShutdown(TimeSpan.FromMilliseconds(300)));
                if (steps.EssentialSucceeded)
                {
                    steps.Run("clean-shutdown mark", () => markedClean = true);
                }

                steps.Run("mutexes", () => mutexesReleased = true);
                return steps;
            }, ct);

            var steps = await exit.WaitAsync(TimeSpan.FromSeconds(5), ct);

            Assert.True(mutexesReleased);
            Assert.False(markedClean);
            Assert.Equal(["GPU 0 fan release"], steps.Failed);
            Assert.False(AppliedStateStore.Load(Uuid)!.CleanShutdown);   // the next launch shows the banner
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public void A_bounded_call_that_never_returns_is_left_on_a_background_thread()
    {
        // The release and the driver teardown run this way at exit. The caller
        // gets its answer on time, and the stuck call is on a background thread,
        // which .NET does not wait for when the process ends.
        using var hold = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        bool? onBackgroundThread = null;
        int callerThread = Environment.CurrentManagedThreadId;
        int? callThread = null;
        try
        {
            bool returned = BoundedCall.TryRun(() =>
            {
                onBackgroundThread = Thread.CurrentThread.IsBackground;
                callThread = Environment.CurrentManagedThreadId;
                entered.Set();
                hold.Wait(TimeSpan.FromSeconds(30));
                return NvmlReturn.Success;
            }, TimeSpan.FromMilliseconds(200), "test driver call", out var rc);

            Assert.False(returned);
            Assert.Equal(default, rc);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(onBackgroundThread);
            Assert.NotEqual(callerThread, callThread);
        }
        finally
        {
            hold.Set();
        }

        // A call that answers in time gives its answer, and its exception.
        Assert.True(BoundedCall.TryRun(() => NvmlReturn.NoPermission, TimeSpan.FromSeconds(10), "test", out var answer));
        Assert.Equal(NvmlReturn.NoPermission, answer);
        Assert.Throws<IOException>(() =>
            BoundedCall.TryRun(() => throw new IOException("driver gone"), TimeSpan.FromSeconds(10), "test"));
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
