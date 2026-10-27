using Afterglow.Core.Fans;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Settings;
using Afterglow.Core.Stress;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tests.Fakes;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

/// <summary>
/// Whole certification runs against a fake tuner and scripted burns — the
/// review's reproductions (R-02, R-03, R-04), made permanent. Each run takes
/// about two seconds: the certifier polls its burn every 500 ms, and the
/// scripted burns report a window already served.
/// </summary>
[Collection("AppPaths")]
public sealed class CertificationRunTests : IDisposable
{
    private readonly StoreScope _scope = new();
    private readonly string _profilesDir =
        Path.Combine(Path.GetTempPath(), $"afterglow-cert-run-{Guid.NewGuid():N}");

    public void Dispose()
    {
        _scope.Dispose();
        try
        {
            Directory.Delete(_profilesDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private ProfileStore Store() => new(_profilesDir);

    private static TuningProfile Daily(int core = 100, int mem = 500) => new()
    {
        Name = "daily",
        GpuUuid = "GPU-TEST-0000",
        CoreOffsetMHz = core,
        MemOffsetMHz = mem,
    };

    private static ProfileCertification Cert(string mode, int core, int mem) => new()
    {
        Mode = mode,
        PassedAt = DateTimeOffset.Now,
        DurationSeconds = 90,
        CoreOffsetMHz = core,
        MemOffsetMHz = mem,
        GpuUuid = "GPU-TEST-0000",
        WorkloadRevision = mode == CertificationModes.Vram ? null : GpuStressTest.WorkloadRevision,
    };

    /// <summary>
    /// A certifier whose burns are scripted. <paramref name="duringMode"/> runs
    /// inside the burn of the given mode (0 = sustained … 3 = VRAM), on the
    /// certifier's own thread, while that burn is live.
    /// </summary>
    private static ProfileCertifier Certifier(
        WriteTrackingTuner tuner,
        ProfileStore store,
        Func<int, Action?>? duringMode = null,
        Func<int, StressState>? verdict = null)
    {
        int mode = 0;
        var certifier = new ProfileCertifier(tuner, store);
        certifier.StressFactory = pattern =>
        {
            int index = mode++;
            return new ScriptedBurn(pattern)
            {
                OnStart = duringMode?.Invoke(index),
                Verdict = verdict?.Invoke(index) ?? StressState.Stopped,
            };
        };
        certifier.VramFactory = () =>
        {
            int index = mode++;
            return new ScriptedVram
            {
                OnStart = duringMode?.Invoke(index),
                Verdict = verdict?.Invoke(index) ?? StressState.Stopped,
            };
        };
        return certifier;
    }

    private static CertifierStatus Run(ProfileCertifier certifier, TuningProfile profile)
    {
        CertifierStatus? final = null;
        using var done = new ManualResetEventSlim(false);
        certifier.StatusChanged += status =>
        {
            if (!status.Running)
            {
                final = status;
                done.Set();
            }
        };

        certifier.Start(profile, new CertifierOptions { SecondsPerMode = 30 });
        Assert.True(done.Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken),
            "the certifier never reported a terminal status");
        return final!;
    }

    [Fact]
    public void A_clean_run_stamps_all_four_modes_and_marks_the_profile_stable()
    {
        // The baseline every other test departs from — and the guard that the
        // certifier's own writes are never mistaken for another writer's.
        var store = Store();
        var profile = Daily();
        store.Save(profile);
        var tuner = new WriteTrackingTuner();

        var final = Run(Certifier(tuner, store), profile);

        Assert.Equal("done", final.Phase);
        Assert.True(final.Passed);
        var stored = store.Load(profile.Name)!;
        Assert.True(stored.MarkedStable);
        Assert.Equal(CertificationModes.All.Order(), stored.Certifications.Select(c => c.Mode).Order());
    }

    [Fact]
    public void A_run_stamps_the_burn_revision_and_replaces_passes_the_older_burn_earned()
    {
        // A profile certified by the burn before R-01's fix: its three burn
        // passes are stored without a revision and no longer count. A new run
        // says so, then stamps the burn modes with this build's revision — and
        // only them: the VRAM test is not the burn.
        var store = Store();
        var profile = Daily() with
        {
            MarkedStable = true,
            Certifications = [.. CertificationModes.All.Select(mode => Cert(mode, 100, 500) with { WorkloadRevision = null })],
        };
        store.Save(profile);
        Assert.True(store.Load(profile.Name)!.CertifiedWithOlderStressTest);
        var tuner = new WriteTrackingTuner();

        var final = Run(Certifier(tuner, store), profile);

        Assert.Equal("done", final.Phase);
        Assert.Contains(final.Log, line => line.Contains(
            "sustained, transitions, excursions passes were earned with an older stress test", StringComparison.Ordinal));

        var stored = store.Load(profile.Name)!;
        Assert.Equal(CertificationModes.All.Order(), stored.Certifications.Select(c => c.Mode).Order());
        Assert.All(stored.Certifications, c => Assert.Equal(
            c.Mode == CertificationModes.Vram ? null : (int?)GpuStressTest.WorkloadRevision, c.WorkloadRevision));
        Assert.False(stored.CertifiedWithOlderStressTest);
    }

    [Fact]
    public void Another_surface_writing_mid_run_ends_certification_as_interrupted_and_never_as_a_pass()
    {
        // R-02: a profile hotkey (or the Tuning page, a game rule, automation)
        // applies +25 while the transitions mode burns. The old drift check only
        // recognised "everything back at stock", so the remaining modes passed
        // against +25 and the profile was marked stable at +100.
        var store = Store();
        var profile = Daily();
        store.Save(profile);
        var tuner = new WriteTrackingTuner();
        var hotkey = new TuningProfile { Name = "hotkey", CoreOffsetMHz = 25, MemOffsetMHz = 500 };

        var final = Run(
            Certifier(tuner, store, duringMode: mode => mode == 1
                ? () => tuner.WriteFromElsewhere(t => t.Apply(hotkey))
                : null),
            profile);

        Assert.NotEqual(true, final.Passed);
        Assert.Equal("interrupted", final.Phase);

        var stored = store.Load(profile.Name)!;
        Assert.False(stored.MarkedStable);
        Assert.Equal([CertificationModes.Sustained], stored.Certifications.Select(c => c.Mode));

        // The other writer owns the card now: nothing re-applied the tested
        // profile over it, and nothing reset it either.
        Assert.Same(hotkey, tuner.Applies[^1].Profile);
        Assert.Equal(0, tuner.ResetCalls);
        Assert.Equal(25, tuner.CoreOffsetMHz);
        Assert.Contains(final.Log, line => line.Contains("interrupted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_configuration_changed_outside_this_process_is_caught_by_the_readback()
    {
        // R-02, the review's exact case: +100/+500 applied, +25/+500 on the card
        // after the burn (another process; no in-process event reports it). The
        // stock-only check accepted it as unchanged.
        var store = Store();
        var profile = Daily();
        store.Save(profile);
        var tuner = new WriteTrackingTuner();

        var final = Run(
            Certifier(tuner, store, duringMode: mode => mode == 1 ? () => tuner.ChangeSilently(25) : null),
            profile);

        Assert.NotEqual(true, final.Passed);
        Assert.Equal("interrupted", final.Phase);
        var stored = store.Load(profile.Name)!;
        Assert.False(stored.MarkedStable);
        Assert.DoesNotContain(stored.Certifications, c => c.Mode == CertificationModes.Transitions);
        Assert.Contains(final.Log, line => line.Contains("+25 MHz", StringComparison.Ordinal));
    }

    [Fact]
    public void A_same_name_replacement_saved_mid_run_is_never_stamped_or_marked_stable()
    {
        // R-03: certification of A at +100 is running; the user moves the
        // sliders to +500 (without applying) and saves under A. The certifier
        // reloaded by name and marked the untested +500 replacement stable —
        // which startup then auto-applies.
        var store = Store();
        var profile = Daily();
        store.Save(profile);
        var tuner = new WriteTrackingTuner();
        var replacement = profile with { CoreOffsetMHz = 500 };

        var final = Run(
            Certifier(tuner, store, duringMode: mode => mode == 2 ? () => store.Save(replacement) : null),
            profile);

        Assert.NotEqual(true, final.Passed);
        var stored = store.Load(profile.Name)!;
        Assert.Equal(500, stored.CoreOffsetMHz);
        Assert.False(stored.MarkedStable);
        Assert.Empty(stored.Certifications);
        Assert.Contains(final.Log, line => line.Contains("changed", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(StressState.ArtifactDetected)]
    [InlineData(StressState.DeviceLost)]
    public void A_demonstrated_failure_revokes_the_stable_mark_and_the_failed_certificate(StressState verdict)
    {
        // R-04: a profile already certified and marked stable fails its
        // recertification. The failure path reset the card and returned, leaving
        // MarkedStable and all four certificates in place — so startup could
        // auto-apply settings the app had just watched fail.
        var store = Store();
        var profile = Daily() with
        {
            MarkedStable = true,
            Certifications = [.. CertificationModes.All.Select(mode => Cert(mode, 100, 500))],
        };
        store.Save(profile);
        var tuner = new WriteTrackingTuner();

        var final = Run(Certifier(tuner, store, verdict: mode => mode == 0 ? verdict : StressState.Stopped), profile);

        Assert.Equal("failed", final.Phase);
        Assert.Equal(CertificationModes.Sustained, final.FailedMode);
        Assert.Equal(1, tuner.ResetCalls);

        var stored = store.Load(profile.Name)!;
        Assert.False(stored.MarkedStable);
        Assert.False(stored.FullyCertified);
        Assert.DoesNotContain(stored.Certifications, c => c.Mode == CertificationModes.Sustained);

        // Only the mode that failed is revoked: the other three passes still
        // happened, they just no longer add up to a certified profile.
        Assert.Equal(3, stored.Certifications.Count);
    }

    /// <summary>Records what a card's fan service asked the driver for, and answers as told.</summary>
    private sealed class FanDriver
    {
        private readonly Lock _lock = new();
        private readonly List<uint> _duties = [];

        public NvmlReturn SetResult { get; set; } = NvmlReturn.Success;

        public int Releases { get; private set; }

        public IReadOnlyList<uint> Duties
        {
            get
            {
                lock (_lock)
                {
                    return [.. _duties];
                }
            }
        }

        public NvmlReturn SetAll(uint duty)
        {
            lock (_lock)
            {
                if (SetResult == NvmlReturn.Success)
                {
                    _duties.Add(duty);
                }
            }

            return SetResult;
        }

        public NvmlReturn Release()
        {
            Releases++;
            return NvmlReturn.Success;
        }
    }

    private static FanControlService FanService(FanDriver driver) =>
        new(driver.SetAll, driver.Release, "GPU-TEST-0000", 30);

    /// <summary>Wired the way the Profiles page wires it: the live mode and duty, plus the card's saved fan settings.</summary>
    private static CertificationFans CertFans(FanControlService service, FanSettings? saved = null) =>
        new(service, () => GameSessionRestore.CaptureFans(service.Mode, service.CommandedDuty, saved ?? new FanSettings()));

    private static ProfileCertifier WithFans(ProfileCertifier template, WriteTrackingTuner tuner, ProfileStore store, CertificationFans fans) =>
        new(tuner, store, fans: fans)
        {
            StressFactory = template.StressFactory,
            VramFactory = template.VramFactory,
        };

    private static GpuSnapshot Hot(uint tempC) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        DeviceIndex = 0,
        GpuTempC = tempC,
    };

    [Fact]
    public void Certification_burns_under_the_profile_fan_policy_and_puts_the_previous_one_back()
    {
        // R-11: the controller runs the fans at a fixed 100 %; the profile says
        // fixed 30 %. The certifier applied only the clocks, so the burn ran at
        // 100 % and the profile was marked stable for a thermal configuration it
        // never runs with.
        var store = Store();
        var profile = Daily() with { FanMode = FanMode.Fixed, FixedFanPct = 30 };
        store.Save(profile);
        var tuner = new WriteTrackingTuner();
        var driver = new FanDriver();
        using var service = FanService(driver);
        Assert.True(service.SetFixed(100));

        uint? dutyWhileBurning = null;
        var certifier = WithFans(
            Certifier(tuner, store, duringMode: mode => mode == 0
                ? () => dutyWhileBurning = driver.Duties[^1]
                : null),
            tuner, store, CertFans(service));

        var final = Run(certifier, profile);

        Assert.Equal("done", final.Phase);
        Assert.Equal(30u, dutyWhileBurning);
        Assert.Equal(100u, driver.Duties[^1]);          // the controller's 100 % is back
        Assert.Equal(FanControlMode.Fixed, service.Mode);
        Assert.Contains(final.Log, line => line.Contains("Fans put back to fixed 100%", StringComparison.Ordinal));
    }

    [Fact]
    public void A_fan_policy_the_driver_refuses_stops_the_run_before_any_clock_moves()
    {
        var store = Store();
        var profile = Daily() with { FanMode = FanMode.Fixed, FixedFanPct = 30 };
        store.Save(profile);
        var tuner = new WriteTrackingTuner();
        var driver = new FanDriver { SetResult = NvmlReturn.NoPermission };
        using var service = FanService(driver);

        var final = Run(WithFans(Certifier(tuner, store), tuner, store, CertFans(service)), profile);

        Assert.Equal("failed", final.Phase);
        Assert.Equal("fans", final.FailedMode);
        Assert.Empty(tuner.Applies);
        Assert.Empty(store.Load(profile.Name)!.Certifications);

        // The service was put back to firmware control, where it started.
        Assert.Equal(FanControlMode.Auto, service.Mode);
        Assert.True(driver.Releases >= 1);
    }

    [Fact]
    public void A_fan_policy_with_no_fan_control_to_apply_it_is_refused()
    {
        var store = Store();
        var profile = Daily() with { FanMode = FanMode.Fixed, FixedFanPct = 30 };
        store.Save(profile);
        var tuner = new WriteTrackingTuner();

        var final = Run(Certifier(tuner, store), profile);

        Assert.Equal("failed", final.Phase);
        Assert.Equal("fans", final.FailedMode);
        Assert.Empty(tuner.Applies);
    }

    [Fact]
    public void A_curve_profile_burns_under_its_curve()
    {
        var store = Store();
        var curve = new FanCurveConfig();
        var profile = Daily() with { FanMode = FanMode.Curve, FanCurve = curve };
        store.Save(profile);
        var tuner = new WriteTrackingTuner();
        var driver = new FanDriver();
        using var service = FanService(driver);

        // Telemetry reaches the curve while each mode burns, as it does in the app.
        var final = Run(
            WithFans(Certifier(tuner, store, duringMode: _ => () => service.OnSnapshot(Hot(80))), tuner, store, CertFans(service)),
            profile);

        Assert.Equal("done", final.Phase);
        Assert.NotEmpty(driver.Duties);
        Assert.Equal(FanControlMode.Auto, service.Mode);   // back to firmware control, as found
    }

    [Fact]
    public void A_fan_curve_that_never_takes_control_does_not_certify()
    {
        // No temperature ever reaches the curve, so the fans keep doing whatever
        // they did before — the burn did not run under the profile's policy.
        var store = Store();
        var profile = Daily() with { FanMode = FanMode.Curve, FanCurve = new FanCurveConfig() };
        store.Save(profile);
        var tuner = new WriteTrackingTuner();
        var driver = new FanDriver();
        using var service = FanService(driver);

        var final = Run(WithFans(Certifier(tuner, store), tuner, store, CertFans(service)), profile);

        Assert.Equal("interrupted", final.Phase);
        Assert.False(store.Load(profile.Name)!.MarkedStable);
        Assert.Contains(final.Log, line => line.Contains("never took control", StringComparison.Ordinal));
    }

    [Fact]
    public void Fans_another_control_takes_over_mid_run_end_the_run_and_are_left_alone()
    {
        var store = Store();
        var profile = Daily() with { FanMode = FanMode.Fixed, FixedFanPct = 30 };
        store.Save(profile);
        var tuner = new WriteTrackingTuner();
        var driver = new FanDriver();
        using var service = FanService(driver);

        // An automation rule pins the fans at 80 % while the transitions mode burns.
        var final = Run(
            WithFans(Certifier(tuner, store, duringMode: mode => mode == 1 ? () => service.SetFixed(80) : null),
                tuner, store, CertFans(service)),
            profile);

        Assert.Equal("interrupted", final.Phase);
        Assert.Equal(80u, driver.Duties[^1]);   // the newer choice stands
        Assert.Equal([CertificationModes.Sustained], store.Load(profile.Name)!.Certifications.Select(c => c.Mode));
    }

    [Fact]
    public void An_engine_that_could_not_run_revokes_nothing()
    {
        // No adapter, no device: that is the absence of a verdict, not a
        // demonstrated failure, so earlier approvals stand.
        var store = Store();
        var profile = Daily() with
        {
            MarkedStable = true,
            Certifications = [.. CertificationModes.All.Select(mode => Cert(mode, 100, 500))],
        };
        store.Save(profile);
        var tuner = new WriteTrackingTuner();

        var final = Run(Certifier(tuner, store, verdict: mode => mode == 0 ? StressState.Failed : StressState.Stopped), profile);

        Assert.Equal("failed", final.Phase);
        var stored = store.Load(profile.Name)!;
        Assert.True(stored.MarkedStable);
        Assert.Equal(4, stored.Certifications.Count);
    }
}
