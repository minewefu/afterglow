using System.Globalization;
using System.Security.Cryptography;
using Afterglow.Core.Interop.Nvapi;

namespace Afterglow.Cli;

/// <summary>
/// `drs --exe NAME [--cap FPS|off] [--vsync default|on|off] [--low-latency on|off] [--clear]`
/// — per-game NVIDIA driver settings (the same store the NVIDIA Control Panel
/// edits). With only --exe, prints the current values. `--cap 1..1000` caps the
/// game; `--cap 0` (or `off`) turns the limiter off for this game (stored as the
/// driver's explicit "disabled", so a global cap does not apply); without --cap
/// the game's cap is left as it is. --clear removes Afterglow's settings so the
/// game inherits the global ones again.
///   --cleanup [--apply]   find the profile "A" that 1.2.0-beta.4 and earlier left
///                         (names truncated to one letter); a dry run unless
///                         --apply, which deletes exact matches only (admin)
///   --selftest            create a throwaway profile for a random unknown exe
///                         through ApplySettings, read it back, remove it (admin)
/// </summary>
internal static class DrsCommand
{
    private const string Usage =
        "Usage: afterglow-cli drs --exe game.exe [--cap FPS|off] [--vsync default|on|off] " +
        "[--low-latency on|off] [--clear]\n" +
        "         --cap 1..1000 = cap; --cap 0 or off = limiter off for this game (overrides a global cap)\n" +
        "         no --cap = the game's cap is left as it is; --clear = back to the global settings\n" +
        "       afterglow-cli drs --cleanup [--apply]\n" +
        "       afterglow-cli drs --selftest";

    /// <summary>NVAPI_INVALID_USER_PRIVILEGE (nvapi_lite_common.h): the call needs administrator rights.</summary>
    private const NvapiStatus InvalidUserPrivilege = (NvapiStatus)(-137);

    public static int Run(string[] args)
    {
        bool cleanup = args.Contains("--cleanup");
        bool apply = args.Contains("--apply");
        bool selfTest = args.Contains("--selftest");
        if (cleanup || apply || selfTest)
        {
            return RunMaintenance(args, cleanup, apply, selfTest);
        }

        string? exe = null;
        string? vsync = null;
        bool? lowLatency = null;
        bool clear = false;

        // Check first, then parse. `default: break;` below meant `drs --exe
        // game.exe --fps-cap 60` (the option is --cap) performed no write at all
        // and exited 0, so a per-game profile the user believed was applied
        // simply was not.
        if (CliArgs.Validate(args, "drs") is string argError)
        {
            Console.Error.WriteLine(argError);
            return 2;
        }

        // Same rule as --seconds elsewhere: a value that does not parse, or is
        // out of range, is an error — not a silent fall-through to the read-only
        // branch (nothing written, exit 0), and not a silent clamp that turned
        // `--cap -5` into cap=0 (uncapped) and reported it as applied.
        if (ParseCap(args, out int? cap) is string capError)
        {
            Console.Error.WriteLine($"{capError} Nothing was written.");
            return 2;
        }

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--exe" when i + 1 < args.Length:
                    exe = args[++i];
                    break;
                case "--cap":
                    // Parsed and range-checked above; only skip the value here.
                    i++;
                    break;
                case "--vsync" when i + 1 < args.Length:
                    vsync = args[++i].ToLowerInvariant();
                    if (vsync is not ("default" or "on" or "off"))
                    {
                        Console.Error.WriteLine(
                            $"Unknown --vsync value '{vsync}' (expected default, on or off). Nothing was written.");
                        return 2;
                    }

                    break;
                case "--low-latency" when i + 1 < args.Length:
                    // "on" or "off" only: anything else silently meant "off",
                    // so `--low-latency yes` turned the feature OFF and said so
                    // as though that had been asked for.
                    string latencyArg = args[++i].ToLowerInvariant();
                    if (latencyArg is not ("on" or "off"))
                    {
                        Console.Error.WriteLine(
                            $"Unknown --low-latency value '{latencyArg}' (expected on or off). Nothing was written.");
                        return 2;
                    }

                    lowLatency = latencyArg == "on";
                    break;
                case "--clear":
                    clear = true;
                    break;
                default:
                    break;
            }
        }

        if (exe is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            exe += ".exe";
        }

        var drs = DrsApi.TryCreate(out var status);
        if (drs is null)
        {
            Console.Error.WriteLine($"DRS unavailable: {status}");
            return 1;
        }

        if (args.Contains("--probe-create"))
        {
            foreach (string name in ProbeCreateNames())
            {
                Console.WriteLine($"  \"{name}\": {drs.ProbeCreate(name)}");
            }

            return 0;
        }

        if (args.Contains("--probe-list"))
        {
            foreach (string name in drs.ProbeListProfiles("afterglow"))
            {
                Console.WriteLine($"  store contains: \"{name}\"");
            }

            Console.WriteLine("  (end of matches)");
            return 0;
        }

        if (args.Contains("--probe-profile"))
        {
            foreach (string candidate in new[]
                     {
                         "Afterglow - " + exe.ToLowerInvariant(),
                         "Base Profile",
                         "3D App - Default Global Settings",
                     })
            {
                Console.WriteLine($"  FindProfileByName(\"{candidate}\") -> {drs.ProbeFindProfile(candidate)}");
            }

            return 0;
        }

        if (clear)
        {
            // ClearSettings reports a refused lookup or delete and checks the store
            // from a fresh session (R-32); the note says what was done or what failed.
            var rc = drs.ClearSettings(exe, out string clearNote);
            Console.WriteLine(rc == NvapiStatus.Ok
                ? $"Cleared Afterglow-managed driver settings for {exe}: {clearNote}."
                : $"Clear failed: {Describe(rc)} — {clearNote}.");
            return rc == NvapiStatus.Ok ? 0 : 1;
        }

        if (cap is not null || vsync is not null || lowLatency is not null)
        {
            // Start from what's stored so unspecified knobs are preserved: vsync
            // and low latency as the game currently gets them, inherited values
            // included. Not the cap (R1): without --cap it is not written at all,
            // so it stays whatever the game's profile holds, or the global
            // setting. It used to be written as read, which stored an explicit
            // "off" for a game running uncapped (a global cap set later then
            // didn't reach it) and copied an inherited global cap into the game's
            // own profile.
            _ = drs.ReadSettings(exe, out var current);
            var settings = new GameDriverSettings
            {
                FrameCapFps = cap,
                Vsync = vsync ?? current.Vsync,
                LowLatency = lowLatency ?? current.LowLatency,
            };
            var rc = drs.ApplySettings(exe, settings, out string note);
            Console.WriteLine(rc == NvapiStatus.Ok
                ? $"Applied to {exe}: cap={(cap is null ? "unchanged" : DrsApi.CapText(cap))} " +
                  $"vsync={settings.Vsync} low-latency={settings.LowLatency} ({note})"
                : $"Apply failed: {rc} {note}");
            return rc == NvapiStatus.Ok ? 0 : 1;
        }

        var read = drs.ReadSettings(exe, out var stored);
        Console.WriteLine(read == NvapiStatus.Ok
            ? $"{exe}: cap={DescribeStoredCap(stored.FrameCapFps)} vsync={stored.Vsync} low-latency={stored.LowLatency}"
            : $"{exe}: no driver profile found ({read})");
        return 0;
    }

    /// <summary>
    /// `--cap`'s value, parsed as the Profiles page parses its field
    /// (<see cref="Core.Settings.GameRule.ParseFrameCap"/>): 1–1000, or 0 / off.
    /// <paramref name="cap"/> is null when --cap is absent: leave the cap alone.
    /// Unlike the page's field an empty value is refused; leaving --cap out says that.
    /// </summary>
    internal static string? ParseCap(string[] args, out int? cap)
    {
        cap = null;
        int at = Array.IndexOf(args, "--cap");
        if (at < 0)
        {
            return null;
        }

        string? value = at + 1 < args.Length ? args[at + 1] : null;
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
        {
            return "'--cap' needs a value: 1 to 1000 fps, or 0 / off for no cap (leave --cap out to keep the game's cap).";
        }

        return Core.Settings.GameRule.ParseFrameCap(value, out cap) is string error ? $"'--cap': {error}" : null;
    }

    /// <summary>The cap <see cref="DrsApi.ReadSettings"/> found, in words.</summary>
    internal static string DescribeStoredCap(int? cap) => cap switch
    {
        null => "not set for this game (the global setting applies)",
        0 => "off (overrides a global cap)",
        _ => DrsApi.CapText(cap),
    };

    /// <summary>
    /// The hidden --probe-create's two profile names: a bare one, and one shaped
    /// like the profiles ApplySettings creates ("Afterglow - exe"). Both carry a
    /// fresh random suffix (R-33): the second used to be the fixed
    /// "Afterglow - afterglow-drstest.exe", so a profile already holding that
    /// name was the one the probe deleted.
    /// </summary>
    internal static string[] ProbeCreateNames()
    {
        string suffix = RandomNumberGenerator.GetHexString(8, lowercase: true);
        return [$"AfterglowProbe-{suffix}", DrsApi.ProfileNameFor($"afterglow-drstest-{suffix}.exe")];
    }

    private static int RunMaintenance(string[] args, bool cleanup, bool apply, bool selfTest)
    {
        // These modes take no other options: a mix is refused rather than guessed
        // at, so `--exe x --apply` can never be read as a cleanup or vice versa.
        bool extra = args.Skip(1).Any(a => a is not ("--cleanup" or "--apply" or "--selftest"));
        if (extra || (apply && !cleanup) || (cleanup && selfTest))
        {
            Console.Error.WriteLine("--cleanup [--apply] and --selftest each stand alone.");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        bool writes = apply || selfTest;
        if (writes && !SelfTest.IsElevated())
        {
            Console.Error.WriteLine(selfTest
                ? "drs --selftest writes to the driver settings store, which needs administrator rights. " +
                  "Run it from an elevated terminal. Nothing was changed."
                : "drs --cleanup --apply deletes driver profiles, which needs administrator rights. " +
                  "Run it from an elevated terminal (the dry run, without --apply, works unelevated). " +
                  "Nothing was changed.");
            return 1;
        }

        var drs = DrsApi.TryCreate(out var status);
        if (drs is null)
        {
            Console.Error.WriteLine($"DRS unavailable: {status}");
            return 1;
        }

        return selfTest ? RunSelfTest(drs) : RunCleanup(drs, apply);
    }

    /// <summary>
    /// `drs --cleanup [--apply]`. Phase 1 (both modes) is a read-only scan; a dry
    /// run returns straight after it. Only phase 2 — reached with --apply alone —
    /// calls anything that writes (<see cref="DrsApi.DeleteProfiles"/>, which
    /// re-checks every match inside the deleting session before deleting it).
    /// </summary>
    private static int RunCleanup(DrsApi drs, bool apply)
    {
        Console.WriteLine(apply
            ? "DRS cleanup: deleting leftovers of the 1.2.0-beta.4 name truncation."
            : "DRS cleanup (dry run — nothing is changed; add --apply, elevated, to delete):");

        // Phase 1 — read-only: ScanProfiles enumerates and reads, never sets, deletes or saves.
        var rc = drs.ScanProfiles(DrsProfileSignatures.IsCleanupCandidate, out var scan);
        if (rc != NvapiStatus.Ok)
        {
            Console.Error.WriteLine($"Reading the driver profile store failed after {scan.Scanned} profiles: {Describe(rc)}. " +
                                    "Nothing was changed.");
            return 1;
        }

        Console.WriteLine($"  {scan.Scanned} profiles scanned; {scan.Profiles.Count} with a one-character name.");
        if (scan.Unreadable > 0)
        {
            Console.WriteLine($"  Note: {scan.Unreadable} profiles could not be read (GetProfileInfo failed) and were not examined.");
        }

        var matches = new List<DrsProfileSnapshot>();
        foreach (var profile in scan.Profiles)
        {
            var verdict = DrsProfileSignatures.EvaluateTruncationLeftover(profile);
            if (verdict.Matches)
            {
                matches.Add(profile);
                PrintProfile("MATCH", profile);
                Console.WriteLine($"      {(apply ? "will delete" : "would delete")}: {verdict.Reason}");
            }
            else
            {
                Console.WriteLine($"  left alone: \"{profile.Name}\" ({Summary(profile)}) — {verdict.Reason}");
            }
        }

        if (matches.Count == 0)
        {
            Console.WriteLine("  No leftover profile found — nothing to delete.");
            return 0;
        }

        if (!apply)
        {
            Console.WriteLine($"Dry run: would delete {matches.Count} profile(s). Nothing was changed. " +
                              "To delete, run `afterglow-cli drs --cleanup --apply` from an elevated terminal.");
            return 0;
        }

        // Phase 2 — --apply only.
        var names = matches.Select(m => m.Name).ToList();
        var deleteRc = drs.DeleteProfiles(
            names, p => DrsProfileSignatures.EvaluateTruncationLeftover(p).Matches, out var outcome);
        foreach (string name in outcome.Refused)
        {
            Console.WriteLine($"  skipped \"{name}\": it no longer matched when re-read for deletion — left untouched.");
        }

        foreach (string name in outcome.NotFound)
        {
            Console.WriteLine($"  skipped \"{name}\": it was gone when re-read for deletion.");
        }

        if (deleteRc != NvapiStatus.Ok)
        {
            Console.WriteLine($"  Delete failed: {Describe(deleteRc)}. Re-reading the store to report what it holds now.");
        }

        foreach (string name in outcome.Deleted)
        {
            Console.WriteLine($"  deleted \"{name}\" and saved.");
        }

        // Phase 3 — read-only again, from fresh sessions, whatever phase 2 reported:
        // the result is what the store holds, not what the calls returned.
        int stillThere = 0;
        foreach (string name in names)
        {
            var check = drs.ReadProfile(name, out var survivor);
            if (check == NvapiStatus.ProfileNotFound)
            {
                Console.WriteLine($"  verified: \"{name}\" is gone (FindProfileByName -> ProfileNotFound).");
            }
            else
            {
                stillThere++;
                Console.WriteLine(survivor is null
                    ? $"  NOT verified: looking \"{name}\" up again returned {Describe(check)}."
                    : $"  still present: \"{survivor.Name}\" ({Summary(survivor)}).");
            }
        }

        var rescanRc = drs.ScanProfiles(DrsProfileSignatures.IsCleanupCandidate, out var after);
        int remaining = after.Profiles.Count(p => DrsProfileSignatures.EvaluateTruncationLeftover(p).Matches);
        Console.WriteLine(rescanRc == NvapiStatus.Ok
            ? $"  Re-scan: {after.Scanned} profiles, {remaining} matching leftover(s) remain."
            : $"  Re-scan failed: {Describe(rescanRc)} — could not confirm that no leftover remains.");

        bool ok = deleteRc == NvapiStatus.Ok && stillThere == 0 && rescanRc == NvapiStatus.Ok && remaining == 0;
        Console.WriteLine(ok
            ? $"Done: {outcome.Deleted.Count} profile(s) deleted and verified gone."
            : $"Finished with problems: {outcome.Deleted.Count} deleted, {stillThere} of {names.Count} still present, " +
              $"{remaining} matching leftover(s) after the re-scan.");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// `drs --selftest`: the unknown-exe path end to end on real hardware —
    /// ApplySettings (the call the app makes) creates "Afterglow - exe", the
    /// values and full names are read back, and the run's own profile is deleted.
    /// The exe name is random and must be unknown to the driver before anything is
    /// written, so the profile name proves what the run created. Cleanup runs
    /// whenever anything may have been written, even after a failed step.
    /// </summary>
    private static int RunSelfTest(DrsApi drs)
    {
        string exe = $"afterglow-drs-selftest-{RandomNumberGenerator.GetHexString(8, lowercase: true)}.exe";
        string profileName = DrsApi.ProfileNameFor(exe);
        var wanted = new GameDriverSettings { FrameCapFps = 117 };
        int failures = 0;

        void Step(bool pass, string what, string detail)
        {
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {what} — {detail}");
            if (!pass)
            {
                failures++;
            }
        }

        Console.WriteLine("DRS self-test: create a profile for an unknown exe, read it back, remove it.");
        Console.WriteLine($"  exe:     {exe}");
        Console.WriteLine($"  profile: {profileName}");

        // 1. The name must be unknown, or a write could land on someone else's profile.
        var appRc = drs.ReadApplicationProfile(exe, out var owner, out _);
        var profileRc = drs.ReadProfile(profileName, out _);
        bool unknown = appRc == NvapiStatus.ExecutableNotFound && profileRc == NvapiStatus.ProfileNotFound;
        Step(unknown, "exe and profile are unknown to the driver",
            $"FindApplicationByName -> {Describe(appRc)}{(owner is null ? string.Empty : $" (in \"{owner.Name}\")")}, " +
            $"FindProfileByName -> {Describe(profileRc)}");
        if (!unknown)
        {
            Console.WriteLine("Self-test aborted before writing anything: nothing was changed.");
            return 1;
        }

        bool applied = false;
        bool namesOk = false;
        try
        {
            // 2. Write through the same public path the app uses.
            var rc = drs.ApplySettings(exe, wanted, out string note);
            applied = rc == NvapiStatus.Ok;
            Step(applied, $"ApplySettings(cap {wanted.FrameCapFps} fps)",
                $"{Describe(rc)}{(note.Length > 0 ? $", {note}" : string.Empty)}");

            // 3. Independent read-back.
            rc = drs.ReadSettings(exe, out var actual);
            var expected = new GameDriverSettings { FrameCapFps = wanted.FrameCapFps, Vsync = "default", LowLatency = false };
            Step(rc == NvapiStatus.Ok && actual == expected, "ReadSettings matches what was written",
                rc == NvapiStatus.Ok
                    ? $"cap={actual.FrameCapFps} vsync={actual.Vsync} low-latency={actual.LowLatency}"
                    : Describe(rc));

            // 4. The stored names are the full strings, not the old one-letter truncation.
            rc = drs.ReadApplicationProfile(exe, out var stored, out string storedApp);
            namesOk = rc == NvapiStatus.Ok && stored is not null &&
                           string.Equals(stored.Name, profileName, StringComparison.Ordinal) &&
                           !stored.IsPredefined &&
                           string.Equals(storedApp, exe, StringComparison.OrdinalIgnoreCase) &&
                           stored.Apps.Count == 1 &&
                           string.Equals(stored.Apps[0].Name, exe, StringComparison.OrdinalIgnoreCase);
            Step(namesOk, "stored names are the full strings",
                rc != NvapiStatus.Ok || stored is null
                    ? Describe(rc)
                    : $"profile \"{stored.Name}\" ({stored.Name.Length} of {profileName.Length} chars, " +
                      $"{(stored.IsPredefined ? "predefined" : "user-created")}), app \"{storedApp}\" " +
                      $"({storedApp.Length} of {exe.Length} chars), {stored.Apps.Count} app(s) on the profile");
        }
        finally
        {
            failures += CleanUpSelfTest(drs, exe, profileName, applied);
        }

        if (!namesOk)
        {
            // Wherever the write went if not to the full name, the exact-name
            // cleanup above cannot see it; the leftover scan can.
            Console.WriteLine("  The full names were not confirmed: run `afterglow-cli drs --cleanup` to check for " +
                              "a one-letter leftover profile.");
        }

        Console.WriteLine(failures == 0
            ? "Self-test passed: a profile for an unknown exe was created with its full name, read back, and removed."
            : $"Self-test FAILED ({failures} step(s)); see above for anything left behind.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Deletes the self-test's own profile (exact name, re-checked in the deleting
    /// session) and verifies removal from fresh sessions. Returns the failed-step count.
    /// </summary>
    private static int CleanUpSelfTest(DrsApi drs, string exe, string profileName, bool applied)
    {
        int failures = 0;
        var rc = drs.DeleteProfiles([profileName],
            p => DrsProfileSignatures.EvaluateSelfTestProfile(p, exe).Matches, out var outcome);

        // Absent is fine only if the write failed (ApplySettings saves nothing on
        // an early failure); after a reported success it means the write went elsewhere.
        bool absent = rc == NvapiStatus.Ok && outcome.NotFound.Count == 1;
        bool removed = rc == NvapiStatus.Ok && outcome.Deleted.Count == 1;
        bool pass = removed || (absent && !applied);
        string detail = rc != NvapiStatus.Ok ? $"{Describe(rc)} — nothing was saved"
            : removed ? $"deleted \"{outcome.Deleted[0]}\" and saved"
            : absent && !applied ? "no such profile — the failed write persisted nothing"
            : absent ? "no profile by the full name exists although ApplySettings reported success"
            : $"refused: \"{profileName}\" does not look like the self-test's own profile, so it was left untouched";
        Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  remove the self-test profile — {detail}");
        if (!pass)
        {
            failures++;
        }

        // Verify from fresh sessions: neither the profile nor the exe may resolve any more.
        var profileRc = drs.ReadProfile(profileName, out var leftProfile);
        var appRc = drs.ReadApplicationProfile(exe, out var leftOwner, out _);
        bool gone = profileRc == NvapiStatus.ProfileNotFound && appRc == NvapiStatus.ExecutableNotFound;
        Console.WriteLine($"  {(gone ? "PASS" : "FAIL")}  removal verified — FindProfileByName -> {Describe(profileRc)}, " +
                          $"FindApplicationByName -> {Describe(appRc)}");
        if (!gone)
        {
            failures++;
            if (leftOwner is not null)
            {
                // --clear deletes an "Afterglow - " profile outright, or strips the
                // three managed settings from any other profile the exe sits in.
                Console.WriteLine($"  LEFT BEHIND: {exe} is still attached to profile \"{leftOwner.Name}\" " +
                                  $"({Summary(leftOwner)}). Remove it with `afterglow-cli drs --exe {exe} --clear` (elevated).");
            }
            else if (leftProfile is not null)
            {
                Console.WriteLine($"  LEFT BEHIND: profile \"{leftProfile.Name}\" ({Summary(leftProfile)}). " +
                                  "Delete it by name in NVIDIA Profile Inspector.");
            }
        }

        return failures;
    }

    private static void PrintProfile(string label, DrsProfileSnapshot profile)
    {
        Console.WriteLine($"  {label}: \"{profile.Name}\" ({(profile.IsPredefined ? "predefined" : "user-created")})");
        Console.WriteLine($"      apps ({profile.Apps.Count}): " +
                          string.Join(", ", profile.Apps.Select(a => $"\"{a.Name}\"{(a.IsPredefined ? " (predefined)" : string.Empty)}")));
        Console.WriteLine($"      settings ({profile.Settings.Count}): " +
                          (profile.Settings.Count == 0 ? "none" : string.Join("; ", profile.Settings.Select(DescribeSetting))));
    }

    private static string Summary(DrsProfileSnapshot profile) =>
        $"{(profile.IsPredefined ? "predefined" : "user-created")}, {profile.Apps.Count} app(s)" +
        (profile.Apps.Count is > 0 and <= 3 ? $" {string.Join(", ", profile.Apps.Select(a => $"\"{a.Name}\""))}" : string.Empty) +
        $", {profile.Settings.Count} setting(s)";

    private static string DescribeSetting(DrsSettingSnapshot setting)
    {
        string name = setting.Id switch
        {
            DrsApi.FrameRateLimiterId => "frame-rate limiter",
            DrsApi.VsyncModeId => "vsync mode",
            DrsApi.PreRenderedFramesId => "max pre-rendered frames",
            _ => "setting",
        };
        string value = setting.Type != 0
            ? $"(non-DWORD, type {setting.Type})"
            : setting.Id switch
            {
                DrsApi.FrameRateLimiterId => setting.Value == 0 ? "off" : $"{setting.Value} fps",
                DrsApi.VsyncModeId => setting.Value switch
                {
                    DrsApi.VsyncForceOn => "force on",
                    DrsApi.VsyncForceOff => "force off",
                    DrsApi.VsyncApplicationControlled => "application-controlled",
                    _ => $"0x{setting.Value:X8}",
                },
                _ => setting.Value.ToString(CultureInfo.InvariantCulture),
            };
        return $"{name} 0x{setting.Id:X8} = {value}{(setting.IsPredefinedValue ? " (predefined value)" : string.Empty)}";
    }

    private static string Describe(NvapiStatus status) =>
        status == InvalidUserPrivilege ? "InvalidUserPrivilege (needs administrator rights)" : status.ToString();
}
