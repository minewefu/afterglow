using Afterglow.Core.Interop.Nvapi;

namespace Afterglow.Cli;

/// <summary>
/// `drs --exe NAME [--cap FPS] [--vsync default|on|off] [--low-latency on|off] [--clear]`
/// — per-game NVIDIA driver settings (the same store the NVIDIA Control Panel
/// edits). With only --exe, prints the current values.
/// </summary>
internal static class DrsCommand
{
    public static int Run(string[] args)
    {
        string? exe = null;
        int cap = -1;
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
        if (CliArgs.TryInt(args, "--cap", 0, 1000, ref cap) is string capError)
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
            Console.Error.WriteLine(
                "Usage: afterglow-cli drs --exe game.exe [--cap FPS] [--vsync default|on|off] " +
                "[--low-latency on|off] [--clear]");
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
            Console.WriteLine("  " + drs.ProbeCreate($"AfterglowProbe{Environment.TickCount % 100000}"));
            Console.WriteLine("  " + drs.ProbeCreate("Afterglow - afterglow-drstest.exe"));
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
            var rc = drs.ClearSettings(exe);
            Console.WriteLine(rc == NvapiStatus.Ok
                ? $"Cleared Afterglow-managed driver settings for {exe}."
                : $"Clear failed: {rc}");
            return rc == NvapiStatus.Ok ? 0 : 1;
        }

        if (cap >= 0 || vsync is not null || lowLatency is not null)
        {
            // Start from what's stored so unspecified knobs are preserved.
            _ = drs.ReadSettings(exe, out var current);
            var settings = new GameDriverSettings
            {
                FrameCapFps = cap >= 0 ? cap : current.FrameCapFps,
                Vsync = vsync ?? current.Vsync,
                LowLatency = lowLatency ?? current.LowLatency,
            };
            var rc = drs.ApplySettings(exe, settings, out string note);
            Console.WriteLine(rc == NvapiStatus.Ok
                ? $"Applied to {exe}: cap={settings.FrameCapFps} vsync={settings.Vsync} " +
                  $"low-latency={settings.LowLatency} ({note})"
                : $"Apply failed: {rc} {note}");
            return rc == NvapiStatus.Ok ? 0 : 1;
        }

        var read = drs.ReadSettings(exe, out var stored);
        Console.WriteLine(read == NvapiStatus.Ok
            ? $"{exe}: cap={stored.FrameCapFps} (0=off) vsync={stored.Vsync} low-latency={stored.LowLatency}"
            : $"{exe}: no driver profile found ({read})");
        return 0;
    }
}
