using Afterglow.Core.Profiles;
using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// The applied-state store's probe-lock records: written by a probe whose
/// restore failed, kept through the clean-shutdown mark, resolved by a
/// verified release — and never at the expense of the record they share a
/// file with, or of the legacy file a UUID-less NVIDIA tuner relies on.
/// </summary>
[Collection("AppPaths")]
public sealed class ProbeRecordStoreTests : IDisposable
{
    private const string Uuid = "GPU-aaaa1111-2222-3333-4444-555566667777";
    private const string IndexKey = "index:0";

    private readonly StoreScope _store = new();

    public void Dispose() => _store.Dispose();

    private static TuningProfile Profile(string name) => new() { Name = name };

    [Fact]
    public void A_probe_flag_does_not_make_a_clean_record_unclean()
    {
        AppliedStateStore.Record(Profile("Gaming"), true, null, Uuid);
        AppliedStateStore.MarkCleanShutdown();

        AppliedStateStore.RecordProbeLockPending(Uuid);

        var state = AppliedStateStore.Load(Uuid);
        Assert.NotNull(state);
        Assert.True(state!.ProbeLockPending);
        Assert.True(state.CleanShutdown);
        Assert.Equal("Gaming", state.ProfileName);
    }

    [Fact]
    public void The_clean_shutdown_mark_keeps_the_probe_flag_unless_told_to_resolve_it()
    {
        AppliedStateStore.RecordProbeLockPending(Uuid);

        AppliedStateStore.MarkCleanShutdown();
        Assert.True(AppliedStateStore.Load(Uuid)?.ProbeLockPending);

        AppliedStateStore.MarkCleanShutdown(resolveProbeLocks: true);
        Assert.NotEqual(true, AppliedStateStore.Load(Uuid)?.ProbeLockPending);
    }

    [Fact]
    public void Resolving_keeps_fan_state_that_shares_the_record()
    {
        AppliedStateStore.RecordProbeLockPending(Uuid);
        AppliedStateStore.RecordFans("fixed", 80, Uuid);

        AppliedStateStore.ResolveProbeLock(Uuid);

        var state = AppliedStateStore.Load(Uuid);
        Assert.NotNull(state);
        Assert.Equal("fixed", state!.FanMode);
        Assert.Equal(80u, state.FanDuty);
        Assert.False(state.ProbeLockPending);
    }

    [Fact]
    public void Resolving_a_record_born_from_the_probe_removes_it()
    {
        AppliedStateStore.RecordProbeLockPending(Uuid);

        AppliedStateStore.ResolveProbeLock(Uuid);

        Assert.Null(AppliedStateStore.Load(Uuid));
    }

    [Fact]
    public void Resolving_leaves_a_real_records_shutdown_state_as_it_was()
    {
        AppliedStateStore.Record(Profile("Gaming"), true, null, Uuid); // unclean: still applied
        AppliedStateStore.RecordProbeLockPending(Uuid);

        AppliedStateStore.ResolveProbeLock(Uuid);

        var state = AppliedStateStore.Load(Uuid);
        Assert.NotNull(state);
        Assert.False(state!.ProbeLockPending);
        Assert.False(state.CleanShutdown);
        Assert.Equal("Gaming", state.ProfileName);
    }

    [Fact]
    public void Index_keyed_probe_records_never_touch_the_legacy_file()
    {
        // A UUID-less NVIDIA tuner reads and writes ONLY the legacy file.
        AppliedStateStore.Record(Profile("nvidia"), true, 2500, null);

        AppliedStateStore.RecordProbeLockPending(IndexKey);
        Assert.Equal(2500u, AppliedStateStore.Load(null)?.LockedCoreClockMHz);
        Assert.True(File.Exists(AppPaths.AppliedStateFile));
        Assert.Contains(AppliedStateStore.LoadAll(), s => s.GpuUuid == IndexKey && s.ProbeLockPending);

        AppliedStateStore.ResolveProbeLock(IndexKey);
        Assert.Equal(2500u, AppliedStateStore.Load(null)?.LockedCoreClockMHz);
        Assert.DoesNotContain(AppliedStateStore.LoadAll(), s => s.GpuUuid == IndexKey);
    }

    [Fact]
    public void Dismissing_the_banner_removes_an_index_keyed_probe_record_outright()
    {
        AppliedStateStore.RecordProbeLockPending(IndexKey);

        AppliedStateStore.MarkCleanShutdown(resolveProbeLocks: true);

        Assert.DoesNotContain(AppliedStateStore.LoadAll(), s => s.GpuUuid == IndexKey);
    }
}
