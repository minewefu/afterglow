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

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--interval" when i + 1 < args.Length &&
                    int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms):
                    intervalMs = Math.Clamp(ms, 100, 60_000);
                    i++;
                    break;
                case "--csv" when i + 1 < args.Length:
                    csvPath = args[++i];
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

        // NVIDIA GPUs get a fresh, unenriched NVML poller so this command's
        // output stays exactly what it has always been (no NVAPI thermals or
        // RPMs in the CLI); Intel GPUs use the context's IGCL source.
        var pollers = manager.Gpus
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
                if (manager.Gpus[i].Vendor == Core.Hardware.GpuVendor.Intel)
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
        using var logger = csvPath is null ? null : new CsvLogger(csvPath);
        logger?.Start();

        bool stop = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop = true;
        };

        var names = manager.Gpus.Select(g => g.Name).ToArray();
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

            if (!once && !first)
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

        return
            $"{name}  [{DateTime.Now:HH:mm:ss}]{Environment.NewLine}" +
            $"  core {s.CoreClockMHz,5} MHz | mem {s.MemClockMHz,5} MHz | {s.GpuTempC,3} C | " +
            $"{s.PowerW,6:F1} W / {s.PowerLimitW,3:F0} W | P{s.PerfState}{Environment.NewLine}" +
            $"  load {s.GpuUtilPct,3}% gpu {s.MemCtrlUtilPct,3}% memctl | vram {(s.VramUsedBytes ?? 0) / 1024 / 1024,6} MiB | " +
            $"fans {fans,-12}{Environment.NewLine}" +
            $"  throttle: {throttle,-40}";
    }
}
