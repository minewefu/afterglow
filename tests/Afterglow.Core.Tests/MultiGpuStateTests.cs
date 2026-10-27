using Afterglow.Core;
using Afterglow.Core.Profiles;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// Per-GPU applied-state files: two cards must never overwrite each other's
/// record, and the pre-multi-GPU single file must stay readable until it is
/// superseded. Each test redirects AppPaths into a throwaway directory.
/// </summary>
[Collection("AppPaths")]
public sealed class AppliedStateStoreTests : IDisposable
{
    private const string UuidA = "GPU-aaaa1111-2222-3333-4444-555566667777";
    private const string UuidB = "GPU-bbbb1111-2222-3333-4444-555566667777";

    private readonly Fakes.StoreScope _store = new();

    public void Dispose() => _store.Dispose();

    private static TuningProfile Profile(string name, int core = 100) =>
        new() { Name = name, CoreOffsetMHz = core, MemOffsetMHz = 500 };

    [Fact]
    public void Two_gpus_keep_independent_records()
    {
        AppliedStateStore.Record(Profile("card A"), allSucceeded: true, lockWrittenByAfterglow: 2800, UuidA);
        AppliedStateStore.Record(Profile("card B"), allSucceeded: true, lockWrittenByAfterglow: null, UuidB);

        var a = AppliedStateStore.Load(UuidA);
        var b = AppliedStateStore.Load(UuidB);

        Assert.Equal("card A", a!.ProfileName);
        Assert.Equal(2800u, a.LockedCoreClockMHz);
        Assert.Equal("card B", b!.ProfileName);
        Assert.Null(b.LockedCoreClockMHz);
    }

    [Fact]
    public void Legacy_single_file_is_adopted_by_its_card_on_first_start_then_retired()
    {
        // A record written the pre-multi-GPU way (no key, the single file).
        AppliedStateStore.WriteLegacyRecord(new AppliedStateStore.AppliedState("legacy", DateTimeOffset.Now, true, false, 2700));

        // A tuner's first read adopts it into the card's own file and retires it.
        Assert.Equal("legacy", AppliedStateStore.LoadOrAdoptLegacy(UuidA, UuidA)!.ProfileName);
        Assert.False(File.Exists(AppPaths.AppliedStateFile));
        Assert.Equal(2700u, AppliedStateStore.Load(UuidA)!.LockedCoreClockMHz);

        // Later writes go to that file; the whole-store scan reports one record.
        AppliedStateStore.Record(Profile("fresh"), allSucceeded: true, lockWrittenByAfterglow: null, UuidA);
        Assert.Equal("fresh", AppliedStateStore.Load(UuidA)!.ProfileName);
        Assert.Single(AppliedStateStore.LoadAll());
    }

    [Fact]
    public void Clear_removes_only_that_gpus_record()
    {
        AppliedStateStore.Record(Profile("card A"), true, null, UuidA);
        AppliedStateStore.Record(Profile("card B"), true, null, UuidB);

        AppliedStateStore.Clear(UuidA);

        Assert.Null(AppliedStateStore.Load(UuidA));
        Assert.Equal("card B", AppliedStateStore.Load(UuidB)!.ProfileName);
    }

    [Fact]
    public void MarkCleanShutdown_marks_every_record()
    {
        AppliedStateStore.Record(Profile("card A"), true, null, UuidA);
        AppliedStateStore.Record(Profile("card B"), true, null, UuidB);

        AppliedStateStore.MarkCleanShutdown();

        Assert.All(AppliedStateStore.LoadAll(), s => Assert.True(s.CleanShutdown));
    }

    private static int RecoveryCandidates() =>
        AppliedStateStore.LoadAll().Count(s => !s.CleanShutdown || s.ProbeLockPending);

    [Fact]
    public void A_cli_set_on_one_card_leaves_another_cards_unfinished_record_unclean()
    {
        // R-15: the running app has an untested stepper offset on card A.
        AppliedStateStore.RecordPending("live GUI stepper +600", UuidA);

        // `afterglow-cli set --gpu 1 ...` on card B, start to finish.
        var claim = AppliedStateStore.BeginCommand(UuidB);
        AppliedStateStore.RecordPending("cli", UuidB);
        AppliedStateStore.Record(Profile("cli"), true, null, UuidB);
        Assert.True(claim.MarkClean());

        Assert.False(AppliedStateStore.Load(UuidA)!.CleanShutdown);
        Assert.True(AppliedStateStore.Load(UuidB)!.CleanShutdown);
        Assert.Equal(1, RecoveryCandidates());
    }

    [Fact]
    public void A_command_does_not_vouch_for_an_unclean_record_it_found_on_its_own_card()
    {
        // The app crashed (or is running) with tuning on card B; nobody has acknowledged it.
        AppliedStateStore.RecordPending("Gaming", UuidB);

        var claim = AppliedStateStore.BeginCommand(UuidB);
        AppliedStateStore.Record(Profile("cli"), true, null, UuidB);

        Assert.False(claim.MarkClean());
        Assert.False(AppliedStateStore.Load(UuidB)!.CleanShutdown);
    }

    [Fact]
    public void The_keyed_mark_touches_neither_other_cards_nor_an_unadopted_legacy_file()
    {
        AppliedStateStore.WriteLegacyRecord(new AppliedStateStore.AppliedState("legacy", DateTimeOffset.Now, true, false));
        AppliedStateStore.Record(Profile("card A"), true, null, UuidA);
        AppliedStateStore.Record(Profile("card B"), true, null, UuidB);

        AppliedStateStore.MarkCleanShutdown([UuidB]);

        Assert.True(AppliedStateStore.Load(UuidB)!.CleanShutdown);
        Assert.False(AppliedStateStore.Load(UuidA)!.CleanShutdown);
        Assert.Contains(AppliedStateStore.LoadAll(), s => s.ProfileName == "legacy" && !s.CleanShutdown);
    }

    [Fact]
    public void The_keyed_mark_keeps_the_probe_flag_like_the_app_level_one()
    {
        AppliedStateStore.RecordProbeLockPending(UuidB);
        AppliedStateStore.Record(Profile("cli"), true, null, UuidB);

        AppliedStateStore.MarkCleanShutdown([UuidB]);

        var b = AppliedStateStore.Load(UuidB);
        Assert.True(b!.CleanShutdown);
        Assert.True(b.ProbeLockPending);
    }
}

[Collection("AppPaths")]
public class ProfileGpuIdentityTests
{
    [Fact]
    public void Per_gpu_file_names_are_distinct_and_stable()
    {
        string a1 = AppliedStateStore.PathFor("GPU-aaaa1111-2222");
        string a2 = AppliedStateStore.PathFor("GPU-aaaa1111-2222");
        string b = AppliedStateStore.PathFor("GPU-bbbb1111-2222");

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.EndsWith(".json", a1, StringComparison.Ordinal);
    }

    // The V/F curve path rules live in VfCurveIdentityTests: the test that
    // stood here asserted the primary card keeps the shared legacy file, which
    // is the defect F-15/F-55 describe.
}
