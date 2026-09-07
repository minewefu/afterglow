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
    public void An_index_keyed_card_keeps_its_lock_and_its_pin_in_one_record()
    {
        // A UUID-less NVIDIA card: its tuner, its fans and a probe pinning it
        // all file under the index key, so a pin never displaces the lock.
        AppliedStateStore.Record(Profile("nvidia"), true, 2500, IndexKey);

        AppliedStateStore.RecordProbeLockPending(IndexKey);
        var pinned = AppliedStateStore.Load(IndexKey);
        Assert.Equal(2500u, pinned?.LockedCoreClockMHz);
        Assert.True(pinned?.ProbeLockPending);

        AppliedStateStore.ResolveProbeLock(IndexKey);
        var resolved = AppliedStateStore.Load(IndexKey);
        Assert.Equal(2500u, resolved?.LockedCoreClockMHz);
        Assert.False(resolved?.ProbeLockPending);
    }

    [Fact]
    public void An_unstamped_legacy_record_is_adopted_by_an_nvidia_key_once_and_never_by_an_intel_one()
    {
        AppliedStateStore.WriteLegacyRecord(new AppliedStateStore.AppliedState("old build", DateTimeOffset.Now, true, false, 2700));

        Assert.Null(AppliedStateStore.LoadOrAdoptLegacy("INTEL-00:02.0-E20B-0000", "INTEL-00:02.0-E20B-0000"));
        Assert.True(File.Exists(AppPaths.AppliedStateFile));

        var adopted = AppliedStateStore.LoadOrAdoptLegacy(IndexKey, null);
        Assert.Equal(2700u, adopted?.LockedCoreClockMHz);
        Assert.Equal(IndexKey, adopted?.GpuUuid);
        Assert.False(File.Exists(AppPaths.AppliedStateFile));
        Assert.Equal(2700u, AppliedStateStore.Load(IndexKey)?.LockedCoreClockMHz);
        Assert.Single(AppliedStateStore.LoadAll());
    }

    [Fact]
    public void A_legacy_lock_is_merged_into_an_existing_probe_record_on_adoption()
    {
        // The previous layout kept a UUID-less card's lock in the legacy file
        // beside an index-keyed probe record; adoption must keep both facts.
        AppliedStateStore.WriteLegacyRecord(new AppliedStateStore.AppliedState("old build", DateTimeOffset.Now, true, false, 2700));
        AppliedStateStore.RecordProbeLockPending(IndexKey);

        var adopted = AppliedStateStore.LoadOrAdoptLegacy(IndexKey, null);

        Assert.NotNull(adopted);
        Assert.Equal(2700u, adopted!.LockedCoreClockMHz);
        Assert.True(adopted.ProbeLockPending);
        Assert.Equal("old build", adopted.ProfileName);
        Assert.False(adopted.CleanShutdown);
        Assert.False(File.Exists(AppPaths.AppliedStateFile));
        Assert.Equal(2700u, AppliedStateStore.Load(IndexKey)?.LockedCoreClockMHz);
    }

    [Fact]
    public void Recording_a_pin_that_is_already_on_record_says_so_and_writes_nothing()
    {
        Assert.False(AppliedStateStore.RecordProbeLockPending(Uuid));
        var first = File.GetLastWriteTimeUtc(AppliedStateStore.PathFor(Uuid));

        Assert.True(AppliedStateStore.RecordProbeLockPending(Uuid));

        Assert.Equal(first, File.GetLastWriteTimeUtc(AppliedStateStore.PathFor(Uuid)));
    }

    [Fact]
    public void A_legacy_record_stamped_for_another_card_is_listed_but_never_adopted()
    {
        AppliedStateStore.WriteLegacyRecord(
            new AppliedStateStore.AppliedState("other", DateTimeOffset.Now, true, false, 2700, GpuUuid: "GPU-other"));

        Assert.Null(AppliedStateStore.LoadOrAdoptLegacy(Uuid, Uuid));
        Assert.True(File.Exists(AppPaths.AppliedStateFile));
        Assert.Contains(AppliedStateStore.LoadAll(), s => s.GpuUuid == "GPU-other");
    }

    [Fact]
    public void Dismissing_the_banner_removes_an_index_keyed_probe_record_outright()
    {
        AppliedStateStore.RecordProbeLockPending(IndexKey);

        AppliedStateStore.MarkCleanShutdown(resolveProbeLocks: true);

        Assert.DoesNotContain(AppliedStateStore.LoadAll(), s => s.GpuUuid == IndexKey);
    }
}
