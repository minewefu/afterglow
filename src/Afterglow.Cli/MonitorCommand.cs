using System.Globalization;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Telemetry;

namespace Afterglow.Cli;

/// <summary>`afterglow-cli monitor [--interval ms] [--csv file] [--once] [--json]`</summary>
internal static class MonitorCommand
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOut = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        int intervalMs = 1000;
        string? csvPath = null;
        bool once = false;
        bool json = args.Contains("--json");

        // Same contract as every other command: declared options, values that
        // must parse, and NO silent clamping — `--interval 5` used to sample at
        // 100 ms and write a CSV at a cadence nobody asked for, while `set` and
        // `monitor` sat outside the option table the contract test covers.
        if (CliArgs.Validate(args, "monitor") is string argError)
        {
            Console.Error.WriteLine(argError);
            return 2;
        }

        if (CliArgs.TryInt(args, "--interval", 100, 60_000, ref intervalMs) is string intervalError)
        {
            Console.Error.WriteLine(intervalError);
            return 2;
        }

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--interval":
                case "--gpu":
                    i++; // parsed by CliArgs.TryInt / CliGpu.TryParseIndex
                    break;
                case "--csv":
                    csvPath = args[++i]; // presence of the value checked by Validate
                    break;
                case "--once":
                    once = true;
                    break;
                case "--json":
                    break;
                default:
                    Console.Error.WriteLine($"Unknown monitor option '{args[i]}'.");
                    return 2;
            }
        }

        using var manager = new Core.Hardware.GpuManager();
        if (manager.Gpus.Count == 0)
        {
            Console.Error.WriteLine(
                $"No supported GPU found (NVML: {manager.NvmlStatus}, IGCL: {manager.IgclStatus}).");
            return 1;
        }

        // Every other GPU-aware command takes --gpu; monitor did not, so on the
        // hybrid Intel+NVIDIA machines this release exists to support there was
        // no way to watch just one card.
        var selected = manager.Gpus;
        if (!CliGpu.TryParseIndex(args, out _, out string? gpuArgError))
        {
            // Without this an unusable index silently widened the watch to ALL
            // GPUs — the opposite of what was asked for.
            Console.Error.WriteLine(gpuArgError);
            return 2;
        }

        if (CliGpu.ParseIndex(args) is { } wantedIndex)
        {
            var one = manager.Gpus.FirstOrDefault(g => g.Index == wantedIndex);
            if (one is null)
            {
                Console.Error.WriteLine($"GPU {wantedIndex} not found — {manager.Gpus.Count} GPU(s) detected.");
                return 2;
            }

            selected = [one];
        }

        // NVIDIA GPUs get a fresh, unenriched NVML poller so this command's
        // output stays exactly what it has always been (no NVAPI thermals or
        // RPMs in the CLI); Intel GPUs use the context's IGCL source.
        var pollers = selected
            .Select(g => g.Vendor == Core.Hardware.GpuVendor.Nvidia && g.Nvml is { } nvmlDevice
                ? new SensorPoller(nvmlDevice)
                : g.Poller)
            .ToArray();

        if (json || once)
        {
            // Single-snapshot modes: Intel power/utilization only exist as
            // deltas between two monotonic counter reads, so prime those
            // sources and report their second sample. NVIDIA polls once,
            // immediately, exactly as before.
            bool anyIntel = false;
            for (int i = 0; i < pollers.Length; i++)
            {
                if (selected[i].Vendor == Core.Hardware.GpuVendor.Intel)
                {
                    _ = pollers[i].Poll();
                    anyIntel = true;
                }
            }

            if (anyIntel)
            {
                Thread.Sleep(150);
            }
        }

        if (json)
        {
            // Machine-readable single snapshot per GPU (agent-friendly).
            var snapshots = pollers.Select(p => p.Poll()).ToArray();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(snapshots, JsonOut));
            return 0;
        }
        CsvLogger? logger = null;
        try
        {
            if (csvPath is not null)
            {
                logger = new CsvLogger(csvPath);
                logger.Start();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            // `--csv C:\Windows` used to exit 127 with a raw .NET stack trace.
            Console.Error.WriteLine($"Could not open the CSV log at '{csvPath}': {ex.Message}");
            logger?.Dispose();
            return 2;
        }

        using var csvLog = logger;

        bool stop = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop = true;
        };

        var names = selected.Select(g => g.Name).ToArray();
        bool first = true;

        while (!stop)
        {
            var lines = new List<string>();
            for (int i = 0; i < pollers.Length; i++)
            {
                var s = pollers[i].Poll();
                logger?.Log(s);
                lines.Add(Format(names[i], s));
            }

            // Redrawing in place needs a real console. Piped to a file or a CI
            // log there is no cursor to move — Console.CursorTop throws — so the
            // frames are simply appended instead.
            if (!once && !first && !Console.IsOutputRedirected)
            {
                Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - (lines.Count * 4)));
            }

            foreach (string line in lines)
            {
                Console.WriteLine(line);
            }

            first = false;
            if (once)
            {
                break;
            }

            Thread.Sleep(intervalMs);
        }

        if (logger?.CurrentFile is string file)
        {
            Console.WriteLine($"CSV written to {file}");
        }

        return 0;
    }

    private static string Format(string name, GpuSnapshot s)
    {
        string fans = s.FanPercents is { Count: > 0 }
            ? string.Join('/', s.FanPercents.Select(f => f + "%"))
            : "n/a";
        string throttle = s.ThrottleReasons is { } tr && tr != NvmlClocksEventReasons.None
            ? tr.ToString()
            : "-";

        // A sensor this device does not expose reads "—", never a blank column or
        // a fabricated number. Blank fields were indistinguishable from a real
        // reading ("    C" for no temperature sensor), a null perf state printed
        // as a bare "P", and the VRAM column showed 0 MiB for an unknown budget.
        static string N(double? value, string format, int width) =>
            (value is { } d ? d.ToString(format, CultureInfo.InvariantCulture) : "—").PadLeft(width);

        // On UMA the figure is the GPU's shared system-memory budget, not
        // dedicated VRAM; say so rather than filing it under "vram" unqualified.
        string vram = s.VramUsedBytes is { } used
            ? $"{used / 1024 / 1024,6} MiB{(s.MemoryIsShared == true ? " shared" : string.Empty)}"
            : $"{"—",6} MiB";

        return
            $"{name}  [{DateTime.Now:HH:mm:ss}]{Environment.NewLine}" +
            $"  core {N(s.CoreClockMHz, "F0", 5)} MHz | mem {N(s.MemClockMHz, "F0", 5)} MHz | {N(s.GpuTempC, "F0", 3)} C | " +
            $"{N(s.PowerW, "F1", 6)} W / {N(s.PowerLimitW, "F0", 3)} W | " +
            $"P{(s.PerfState is { } perf ? perf.ToString(CultureInfo.InvariantCulture) : "—")}{Environment.NewLine}" +
            $"  load {N(s.GpuUtilPct, "F0", 3)}% gpu {N(s.MemCtrlUtilPct, "F0", 3)}% memctl | vram {vram} | " +
            $"fans {fans,-12}{Environment.NewLine}" +
            $"  throttle: {throttle,-40}";
    }
}
