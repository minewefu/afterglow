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

    [Fact]
    public void Vf_curve_paths_keep_the_primary_on_the_legacy_file()
    {
        Assert.Equal(VfCurveRecorder.DefaultPath, VfCurveRecorder.PathFor("GPU-aaaa", isPrimary: true));
        Assert.NotEqual(VfCurveRecorder.DefaultPath, VfCurveRecorder.PathFor("GPU-aaaa", isPrimary: false));
    }
}
