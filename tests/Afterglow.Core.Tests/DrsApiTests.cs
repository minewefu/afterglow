using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Tests.Fakes;
using static Afterglow.Core.Tests.Fakes.FakeDrsDriver;

namespace Afterglow.Core.Tests;

/// <summary>
/// DrsApi's write, clear and probe workflows end to end against
/// <see cref="FakeDrsDriver"/> — the unchanged production code, bound to an
/// in-memory store instead of nvapi64.dll. What these pin is the logic (what
/// is written, deleted, saved and verified); the fake's driver semantics are
/// NVIDIA's documented ones, not measurements of a live driver.
/// </summary>
[Collection("FakeDrsDriver")]
public class DrsApiTests
{
    private const string Exe = "game.exe";
    private static readonly string OwnProfile = DrsApi.ProfileNameFor(Exe);

    public DrsApiTests() => Reset();

    // ---- R-31: a cap of 0 is "off for this game", not "inherit" ----

    [Fact]
    public void Cap_zero_writes_the_limiter_off_so_a_global_cap_does_not_apply()
    {
        // The review's repro: a 60 fps cap in the global profile, and a rule
        // that asks for no cap. Deleting the setting exposed the inherited 60.
        Global[DrsApi.FrameRateLimiterId] = 60;
        var drs = Create();

        var rc = drs.ApplySettings(Exe, new GameDriverSettings { FrameCapFps = 0, Vsync = "on" }, out string note);

        Assert.Equal(NvapiStatus.Ok, rc);
        Assert.Equal("verified", note);
        var stored = StoredProfile(OwnProfile)!;
        Assert.Equal(new Setting(0, IsPredefined: false), stored.Settings[DrsApi.FrameRateLimiterId]);
        Assert.Equal(NvapiStatus.Ok, drs.ReadSettings(Exe, out var effective));
        Assert.Equal(0, effective.FrameCapFps);
    }

    [Fact]
    public void Cap_zero_turns_an_existing_per_game_cap_off()
    {
        Global[DrsApi.FrameRateLimiterId] = 60;
        var drs = Create();
        Assert.Equal(NvapiStatus.Ok, drs.ApplySettings(Exe, new GameDriverSettings { FrameCapFps = 117 }, out _));

        var rc = drs.ApplySettings(Exe, new GameDriverSettings { FrameCapFps = 0, LowLatency = true }, out string note);

        Assert.Equal(NvapiStatus.Ok, rc);
        Assert.Equal("verified", note);
        Assert.Equal(0u, StoredProfile(OwnProfile)!.Settings[DrsApi.FrameRateLimiterId].Value);
    }

    [Fact]
    public void Clearing_is_what_hands_the_cap_back_to_the_global_setting()
    {
        // Deletion survives as the inherit operation: --clear / removing a rule.
        Global[DrsApi.FrameRateLimiterId] = 60;
        var drs = Create();
        Assert.Equal(NvapiStatus.Ok, drs.ApplySettings(Exe, new GameDriverSettings { FrameCapFps = 0, Vsync = "on" }, out _));

        Assert.Equal(NvapiStatus.Ok, drs.ClearSettings(Exe));

        Assert.Null(StoredProfile(OwnProfile));
        Assert.Equal(NvapiStatus.ExecutableNotFound, drs.ReadSettings(Exe, out _));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(1001)]
    public void An_out_of_range_cap_writes_nothing(int cap)
    {
        // It used to be clamped and written (−5 as 0, 1001 as 1000), then
        // compared against the unclamped request.
        var drs = Create();

        var rc = drs.ApplySettings(Exe, new GameDriverSettings { FrameCapFps = cap }, out string note);

        Assert.Equal(NvapiStatus.InvalidArgument, rc);
        Assert.Contains("nothing was written", note, StringComparison.Ordinal);
        Assert.Empty(Stored);
        Assert.Equal(0, SaveCount);
    }

    [Fact]
    public void A_positive_cap_is_written_and_verified()
    {
        var drs = Create();

        var rc = drs.ApplySettings(Exe, new GameDriverSettings { FrameCapFps = 141 }, out string note);

        Assert.Equal(NvapiStatus.Ok, rc);
        Assert.Equal("verified", note);
        var stored = StoredProfile(OwnProfile)!;
        Assert.Equal([Exe], stored.Apps);
        Assert.Equal(141u, stored.Settings[DrsApi.FrameRateLimiterId].Value);
    }

    // ---- R-32: ClearSettings reports what it could not do ----

    [Fact]
    public void Clear_reports_a_refused_profile_delete_and_saves_nothing()
    {
        var drs = Create();
        Assert.Equal(NvapiStatus.Ok, drs.ApplySettings(Exe, new GameDriverSettings { FrameCapFps = 90 }, out _));
        int saves = SaveCount;
        FailDeleteProfile = InvalidUserPrivilege;

        var rc = drs.ClearSettings(Exe, out string note);

        Assert.Equal(InvalidUserPrivilege, rc);
        Assert.Equal($"deleting profile \"{OwnProfile}\" failed; nothing was changed", note);
        Assert.Equal(saves, SaveCount);
        Assert.Equal(90u, StoredProfile(OwnProfile)!.Settings[DrsApi.FrameRateLimiterId].Value);
    }

    [Fact]
    public void Clear_reports_a_refused_setting_delete_and_saves_nothing()
    {
        Seed(UserProfileWithOverrides());
        var drs = Create();
        FailDeleteProfileSetting = NvapiStatus.Error;

        var rc = drs.ClearSettings(Exe, out string note);

        Assert.Equal(NvapiStatus.Error, rc);
        Assert.Equal("removing the frame-rate limiter from \"Some Game\" failed; nothing was changed", note);
        Assert.Equal(0, SaveCount);
        Assert.Equal(4, StoredProfile("Some Game")!.Settings.Count);
    }

    [Fact]
    public void Clear_on_someone_elses_profile_removes_only_the_managed_settings()
    {
        // Only two of the three managed settings are set: the third delete is
        // SETTING_NOT_FOUND, which means "nothing to remove", not a failure. The
        // global cap the game inherits afterwards is not an override either.
        var profile = UserProfileWithOverrides();
        profile.Settings.Remove(DrsApi.PreRenderedFramesId);
        Seed(profile);
        Global[DrsApi.FrameRateLimiterId] = 60;
        var drs = Create();

        var rc = drs.ClearSettings(Exe, out string note);

        Assert.Equal(NvapiStatus.Ok, rc);
        Assert.Equal("removed Afterglow's settings from \"Some Game\", saved and verified", note);
        var stored = StoredProfile("Some Game")!;
        Assert.Equal([Exe], stored.Apps);
        Assert.Equal([UnmanagedSettingId], stored.Settings.Keys);
    }

    [Fact]
    public void Clear_accepts_a_predefined_value_coming_back()
    {
        // NVIDIA: DeleteProfileSetting "deletes a setting or sets it back to
        // predefined value" — the driver's own value is not an override.
        Seed(new Profile
        {
            Name = "Shipped Game Profile",
            IsPredefined = true,
            Apps = [Exe],
            Settings = { [DrsApi.VsyncModeId] = new Setting(DrsApi.VsyncForceOn, IsPredefined: false) },
            PredefinedValues = { [DrsApi.VsyncModeId] = DrsApi.VsyncForceOff },
        });
        var drs = Create();

        var rc = drs.ClearSettings(Exe);

        Assert.Equal(NvapiStatus.Ok, rc);
        Assert.Equal(new Setting(DrsApi.VsyncForceOff, IsPredefined: true),
            StoredProfile("Shipped Game Profile")!.Settings[DrsApi.VsyncModeId]);
    }

    [Fact]
    public void Clear_propagates_a_failed_lookup_instead_of_calling_it_nothing_to_clear()
    {
        Seed(UserProfileWithOverrides());
        var drs = Create();
        FailFindApplication = NvapiStatus.Error;

        var rc = drs.ClearSettings(Exe);

        Assert.Equal(NvapiStatus.Error, rc);
        Assert.Equal(0, SaveCount);
    }

    [Fact]
    public void Clear_with_no_profile_for_the_exe_is_ok_and_writes_nothing()
    {
        var drs = Create();

        Assert.Equal(NvapiStatus.Ok, drs.ClearSettings(Exe));
        Assert.Equal(0, SaveCount);
    }

    [Fact]
    public void Clear_propagates_an_unreadable_profile_instead_of_guessing_whose_it_is()
    {
        // An unreadable name used to read as "not Afterglow's": the settings were
        // stripped, the profile kept, and Ok returned.
        var drs = Create();
        Assert.Equal(NvapiStatus.Ok, drs.ApplySettings(Exe, new GameDriverSettings { FrameCapFps = 90 }, out _));
        int saves = SaveCount;
        FailGetProfileInfo = NvapiStatus.Error;

        var rc = drs.ClearSettings(Exe);

        Assert.Equal(NvapiStatus.Error, rc);
        Assert.Equal(saves, SaveCount);
        Assert.Equal(90u, StoredProfile(OwnProfile)!.Settings[DrsApi.FrameRateLimiterId].Value);
    }

    [Fact]
    public void Clear_fails_when_the_driver_reports_a_delete_it_did_not_do()
    {
        var drs = Create();
        Assert.Equal(NvapiStatus.Ok, drs.ApplySettings(Exe, new GameDriverSettings { FrameCapFps = 90 }, out _));
        IgnoreDeleteProfile = true;

        var rc = drs.ClearSettings(Exe, out string note);

        Assert.Equal(NvapiStatus.Error, rc);
        Assert.Equal($"deleted profile \"{OwnProfile}\" and saved, but the frame-rate limiter is still set for {Exe}", note);
    }

    [Fact]
    public void Clear_fails_when_a_managed_setting_survives_its_delete()
    {
        Seed(UserProfileWithOverrides());
        var drs = Create();
        IgnoreDeleteProfileSetting = true;

        var rc = drs.ClearSettings(Exe, out string note);

        Assert.Equal(NvapiStatus.Error, rc);
        Assert.EndsWith("but the frame-rate limiter, vsync, max pre-rendered frames are still set for game.exe", note,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_reports_a_refused_save()
    {
        Seed(UserProfileWithOverrides());
        var drs = Create();
        FailSave = InvalidUserPrivilege;

        Assert.Equal(InvalidUserPrivilege, drs.ClearSettings(Exe));
        Assert.Equal(4, StoredProfile("Some Game")!.Settings.Count);
    }

    // ---- R-33: the creation probe deletes only what it created ----

    [Fact]
    public void Probe_create_never_deletes_a_profile_that_already_had_the_name()
    {
        // The review's repro: the probe's name is taken, creation fails with
        // PROFILE_NAME_IN_USE, and the probe used to delete the existing
        // profile anyway and save the deletion.
        const string Taken = "Afterglow - afterglow-drstest.exe";
        Seed(new Profile
        {
            Name = Taken,
            Apps = ["afterglow-drstest.exe"],
            Settings = { [DrsApi.FrameRateLimiterId] = new Setting(60, IsPredefined: false) },
        });
        var drs = Create();

        string report = drs.ProbeCreate(Taken);

        Assert.Contains($"create={NvapiStatus.ProfileNameInUse}", report, StringComparison.Ordinal);
        Assert.Equal(0, DeleteProfileCalls);
        Assert.Equal(0, SaveCount);
        Assert.Equal(["afterglow-drstest.exe"], StoredProfile(Taken)!.Apps);
    }

    [Fact]
    public void Probe_create_removes_the_profile_it_created()
    {
        const string Name = "AfterglowProbe-0123abcd";
        var drs = Create();

        string report = drs.ProbeCreate(Name);

        Assert.Contains("create=Ok save=Ok refind=Ok", report, StringComparison.Ordinal);
        Assert.Contains("delete+save=Ok", report, StringComparison.Ordinal);
        Assert.Equal(1, DeleteProfileCalls);
        Assert.Null(StoredProfile(Name));
    }

    [Fact]
    public void Probe_create_deletes_nothing_when_its_save_failed()
    {
        // The create lived only in the session, which was destroyed unsaved:
        // whatever has the name now is not this probe's.
        const string Name = "AfterglowProbe-0123abcd";
        var drs = Create();
        FailSave = InvalidUserPrivilege;

        string report = drs.ProbeCreate(Name);

        Assert.Contains($"save={InvalidUserPrivilege}", report, StringComparison.Ordinal);
        Assert.Equal(0, DeleteProfileCalls);
        Assert.Empty(Stored);
    }

    [Fact]
    public void Probe_create_leaves_the_name_alone_once_it_is_no_longer_its_bare_profile()
    {
        // Another process attaches an exe to the probe's profile between the
        // probe's create and its cleanup: no longer only the probe's, so kept.
        const string Name = "AfterglowProbe-0123abcd";
        var drs = Create();
        AfterNextSave = () => StoredProfile(Name)?.Apps.Add("someone.exe");

        string report = drs.ProbeCreate(Name);

        Assert.Contains("delete=refused", report, StringComparison.Ordinal);
        Assert.Equal(0, DeleteProfileCalls);
        Assert.Equal(["someone.exe"], StoredProfile(Name)!.Apps);
    }

    [Fact]
    public void Probe_create_names_are_fresh_on_every_run()
    {
        string[] first = Afterglow.Cli.DrsCommand.ProbeCreateNames();
        string[] second = Afterglow.Cli.DrsCommand.ProbeCreateNames();

        Assert.Equal(2, first.Length);
        Assert.Matches("^AfterglowProbe-[0-9a-f]{8}$", first[0]);
        Assert.Matches(@"^Afterglow - afterglow-drstest-[0-9a-f]{8}\.exe$", first[1]);
        Assert.DoesNotContain("Afterglow - afterglow-drstest.exe", first);
        Assert.Empty(first.Intersect(second));
    }

    /// <summary>A setting Afterglow does not manage (it must survive a clear).</summary>
    private const uint UnmanagedSettingId = 0x1057EB71;

    /// <summary>A user-made profile (not "Afterglow - …") holding the exe, all three managed settings and one other.</summary>
    private static Profile UserProfileWithOverrides() => new()
    {
        Name = "Some Game",
        Apps = [Exe],
        Settings =
        {
            [DrsApi.FrameRateLimiterId] = new Setting(60, IsPredefined: false),
            [DrsApi.VsyncModeId] = new Setting(DrsApi.VsyncForceOff, IsPredefined: false),
            [DrsApi.PreRenderedFramesId] = new Setting(1, IsPredefined: false),
            [UnmanagedSettingId] = new Setting(7, IsPredefined: false),
        },
    };
}
