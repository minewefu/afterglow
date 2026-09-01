using System.Text.Json;
using System.Text.Json.Nodes;
using Afterglow.Core.Hardware;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Stress;
using Afterglow.Core.Telemetry;
using Afterglow.Core.Tuning;

namespace Afterglow.Cli;

/// <summary>
/// `afterglow-cli mcp` — a Model Context Protocol server over stdio (newline-
/// delimited JSON-RPC 2.0), so AI agents can tune the GPU with typed tools.
/// Safety is inherited from the engine: every write is clamped to the
/// driver-reported legal range and verified where the driver allows readback,
/// and the burn tool reports bit-exact computation errors and driver resets —
/// giving an agent a truthful stability signal for autonomous tuning loops.
/// Writes require the server to run elevated; results say so when it isn't.
/// </summary>
internal static class McpCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private sealed record ToolDef(string Name, string Description, JsonObject InputSchema, Func<JsonObject?, object> Invoke);

    public static int Run(string[]? args = null)
    {
        using var manager = new GpuManager();

        // `mcp --gpu N` binds the whole server to one card; agents that want
        // another card run a second server. Default: the first NVML device.
        var gpu = manager.Gpus.Count > 0 ? manager.Gpus[0] : null;
        if (args is not null && CliGpu.ParseIndex(args) is { } wantedIndex)
        {
            gpu = manager.Gpus.FirstOrDefault(g => g.Index == wantedIndex);
            if (gpu is null)
            {
                Console.Error.WriteLine(
                    $"GPU {wantedIndex} not found — {manager.Gpus.Count} GPU(s) detected.");
                return 2;
            }
        }

        bool elevated = AppServicesLikeElevationCheck();
        var profiles = new ProfileStore();

        var tools = BuildTools(manager, gpu, profiles, elevated);

        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? request;
            try
            {
                // Tolerate BOM-prefixed lines from shell-piped clients.
                request = JsonNode.Parse(line.TrimStart('﻿', ' ', '\t'));
            }
            catch (JsonException)
            {
                WriteError(null, -32700, "Parse error");
                continue;
            }

            string? method = request?["method"]?.GetValue<string>();
            JsonNode? id = request?["id"];

            switch (method)
            {
                case "initialize":
                    string protocol = request?["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";
                    WriteResult(id, new JsonObject
                    {
                        ["protocolVersion"] = protocol,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "afterglow",
                            ["version"] = typeof(McpCommand).Assembly.GetName().Version?.ToString(3) ?? "dev",
                        },
                    });
                    break;

                case "notifications/initialized":
                case "notifications/cancelled":
                    break; // notifications get no response

                case "ping":
                    WriteResult(id, new JsonObject());
                    break;

                case "tools/list":
                    var list = new JsonArray();
                    foreach (var tool in tools)
                    {
                        list.Add(new JsonObject
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description,
                            ["inputSchema"] = tool.InputSchema.DeepClone(),
                        });
                    }

                    WriteResult(id, new JsonObject { ["tools"] = list });
                    break;

                case "tools/call":
                    HandleToolCall(id, request?["params"], tools);
                    break;

                case null:
                    WriteError(id, -32600, "Invalid request");
                    break;

                default:
                    if (id is not null)
                    {
                        WriteError(id, -32601, $"Method not found: {method}");
                    }

                    break;
            }
        }

        return 0;
    }

    private static void HandleToolCall(JsonNode? id, JsonNode? parameters, List<ToolDef> tools)
    {
        string? name = parameters?["name"]?.GetValue<string>();
        var tool = tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
        {
            WriteError(id, -32602, $"Unknown tool: {name}");
            return;
        }

        object outcome;
        bool isError = false;
        try
        {
            outcome = tool.Invoke(parameters?["arguments"] as JsonObject);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            outcome = new { error = ex.Message };
            isError = true;
        }

        string payload = JsonSerializer.Serialize(outcome, Json);

        // The MCP-standard flag must agree with the body: an agent checking
        // only isError must not conclude a failed apply succeeded. Indexing is
        // only valid on JSON objects — a tool returning an array or scalar
        // must not take down the server's request loop.
        if (!isError)
        {
            try
            {
                if (JsonNode.Parse(payload) is JsonObject body)
                {
                    isError = body["error"] is not null ||
                              body["allSucceeded"]?.GetValue<bool>() == false ||
                              body["all_succeeded"]?.GetValue<bool>() == false;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
            }
        }

        WriteResult(id, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload,
                },
            },
            ["isError"] = isError,
        });
    }

    private static List<ToolDef> BuildTools(GpuManager manager, GpuContext? gpu, ProfileStore profiles, bool elevated)
    {
        JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] fields)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var field in fields)
            {
                properties[field.Name] = new JsonObject
                {
                    ["type"] = field.Type,
                    ["description"] = field.Description,
                };
                if (field.Required)
                {
                    required.Add(field.Name);
                }
            }

            var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
            if (required.Count > 0)
            {
                schema["required"] = required;
            }

            return schema;
        }

        object RequireGpu()
        {
            return new { error = $"No supported GPU available (NVML: {manager.NvmlStatus}, IGCL: {manager.IgclStatus})." };
        }

        return
        [
            new ToolDef(
                "get_capabilities",
                "GPU identity and the driver-reported legal tuning ranges. All writes are clamped to these " +
                "ranges by the engine — values outside them are impossible to apply.",
                Schema(),
                _ => gpu is null ? RequireGpu() : new
                {
                    gpu = gpu.Name,
                    driver = gpu.DriverVersion,
                    architecture = gpu.Architecture,
                    elevated,
                    write_access = elevated,
                    capabilities = gpu.Tuner.Capabilities,
                }),

            new ToolDef(
                "get_telemetry",
                "One full sensor snapshot: clocks, temperatures (incl. memory junction where exposed), " +
                "instantaneous board power, utilization, VRAM, fans (duty + RPM), throttle reasons, and " +
                "°C of headroom to throttle.",
                Schema(),
                _ => gpu is null ? RequireGpu() : (object)gpu.Poller.Poll()),

            new ToolDef(
                "apply_tuning",
                "Apply tuning knobs (requires the server to run elevated). Omitted values keep their current " +
                "state; unlock=true removes the clock lock. Every value is clamped to the driver range and " +
                "readback-verified where possible; the per-knob results say exactly what happened.",
                Schema(
                    ("core_offset_mhz", "integer", "Core clock offset in MHz", false),
                    ("mem_offset_mhz", "integer", "Memory clock offset in MHz", false),
                    ("power_limit_w", "number", "Board power limit in watts", false),
                    ("lock_clock_mhz", "integer", "Cap boost at this clock (undervolt lock)", false),
                    ("unlock", "boolean", "Remove any clock lock", false),
                    ("voltage_boost_pct", "integer", "Core voltage boost percent 0-100", false),
                    ("fan", "string", "\"auto\" or a duty percentage 0-100 (0 = stop)", false)),
                args => gpu is null ? RequireGpu() : ApplyTuning(gpu, args)),

            new ToolDef(
                "reset_defaults",
                "Return every knob (offsets, power, voltage, lock, fans) to driver defaults. The safe abort.",
                Schema(),
                _ => gpu is null ? RequireGpu() : (object)new { results = gpu.Tuner.ResetToDefaults().Results }),

            new ToolDef(
                "run_stress",
                "Run the bit-exact burn test while sampling telemetry. Returns pass/fail (computation errors " +
                "or a driver reset = current clocks unstable), dispatch throughput (a relative performance " +
                "score), and peak temperature/power/clock seen during the burn. This is the ground-truth " +
                "signal for an autonomous tuning loop: apply offset -> run_stress -> check state/errors -> step.",
                Schema(
                    ("seconds", "integer", "Burn duration, 5-600 (default 30)", false),
                    ("intensity", "integer", "Load knob 512-16384 (default 4096)", false),
                    ("pattern", "string",
                        "Load shape: 'sustained' (default, full load), 'transitions' (load/idle cycling that " +
                        "forces memory-clock transitions — catches memory offsets that pass sustained burns " +
                        "but crash at the desktop; VRAM retention is verified across each transition), or " +
                        "'excursions' (short saturating bursts riding the boost overshoot through the top " +
                        "clock bins — the bursty desktop regime sustained burns never exercise). Validate " +
                        "all three before trusting a daily config.",
                        false)),
                args => gpu is null ? RequireGpu() : RunStress(gpu, args)),

            new ToolDef(
                "find_stable_offset",
                "The guided stability stepper as one autonomous call: steps the core offset up, burn-testing " +
                "each step with bit-exact verification, backs off on the first failure, and runs a longer " +
                "confirmation burn. BLOCKS until finished (typically several minutes). Requires elevation. " +
                "The confirmed stable offset is left applied and returned with the step-by-step log.",
                Schema(
                    ("step_mhz", "integer", "MHz added per step, 5-60 (default 30)", false),
                    ("seconds_per_step", "integer", "Burn seconds per step, 30-300 (default 60)", false),
                    ("max_offset_mhz", "integer", "Highest offset to try, 50-600 (default 300)", false),
                    ("max_minutes", "integer",
                        "Wall-clock budget, 5-120 (default 30); on expiry the run is cancelled and the starting offset restored", false)),
                args => gpu is null ? RequireGpu() : RunStepper(gpu, args)),

            new ToolDef(
                "run_vram_test",
                "Full-capacity VRAM test: fills as much of the card's memory as the OS safely allows with a " +
                "deterministic pattern and verifies every element on the GPU (alternate rounds invert the " +
                "pattern). Catches memory-offset errors the bandwidth burn can't. Returns coverage, rounds, " +
                "and errors; any error means the current memory clocks are unstable.",
                Schema(("seconds", "integer", "Test window, 15-1800 (default 90); always completes at least one full round", false)),
                args => gpu is null ? RequireGpu() : RunVramTest(gpu, args)),

            new ToolDef(
                "list_profiles",
                "Saved tuning profiles.",
                Schema(),
                _ => new { profiles = profiles.LoadAll() }),

            new ToolDef(
                "save_profile",
                "Save the currently applied tuning as a named profile (e.g., after a successful tuning loop).",
                Schema(("name", "string", "Profile name", true)),
                args =>
                {
                    if (gpu is null)
                    {
                        return RequireGpu();
                    }

                    string name = args?["name"]?.GetValue<string>() ?? "agent";
                    var (core, mem, power, boost, lockMhz) = gpu.Tuner.ReadCurrent();
                    var profile = new TuningProfile
                    {
                        Name = name,
                        CoreOffsetMHz = core,
                        MemOffsetMHz = mem,
                        PowerLimitW = power > 0 ? power : null,
                        VoltageBoostPct = boost,
                        LockedCoreClockMHz = lockMhz,
                        Notes = $"Saved via MCP {DateTimeOffset.Now:u}",
                    };
                    profiles.Save(profile);
                    return new { saved = name, profile };
                }),

            new ToolDef(
                "apply_profile",
                "Apply a saved profile's clock/power/voltage knobs (requires elevation).",
                Schema(("name", "string", "Profile name", true)),
                args =>
                {
                    if (gpu is null)
                    {
                        return RequireGpu();
                    }

                    string name = args?["name"]?.GetValue<string>() ?? string.Empty;
                    var profile = profiles.Load(name);
                    if (profile is null)
                    {
                        return new { error = $"Profile '{name}' not found." };
                    }

                    var result = gpu.Tuner.Apply(profile);
                    return new { applied = name, all_succeeded = result.AllSucceeded, results = result.Results };
                }),
        ];
    }

    private static object ApplyTuning(GpuContext gpu, JsonObject? args)
    {
        var current = gpu.Tuner.ReadCurrent();
        bool unlock = args?["unlock"]?.GetValue<bool>() ?? false;

        var profile = new TuningProfile
        {
            Name = "mcp",
            CoreOffsetMHz = args?["core_offset_mhz"]?.GetValue<int>() ?? current.CoreOffsetMHz,
            MemOffsetMHz = args?["mem_offset_mhz"]?.GetValue<int>() ?? current.MemOffsetMHz,
            PowerLimitW = args?["power_limit_w"]?.GetValue<double>(),
            VoltageBoostPct = args?["voltage_boost_pct"]?.GetValue<uint>(),
            LockedCoreClockMHz = unlock
                ? null
                : args?["lock_clock_mhz"]?.GetValue<uint>() ?? current.LockedCoreClockMHz,
        };

        // Schema validation errors cite generic sanity bounds; report the
        // ranges that actually matter — this GPU's — alongside them.
        if (profile.Validate() is string validationError)
        {
            var caps = gpu.Tuner.Capabilities;
            return new
            {
                all_succeeded = false,
                error = validationError,
                driver_ranges_for_this_gpu = new
                {
                    core_offset_mhz = $"{caps.CoreOffsetMinMHz}..{caps.CoreOffsetMaxMHz}",
                    mem_offset_mhz = $"{caps.MemOffsetMinMHz}..{caps.MemOffsetMaxMHz}",
                    power_limit_w = $"{caps.PowerLimitMinW:F0}..{caps.PowerLimitMaxW:F0}",
                },
            };
        }

        var result = gpu.Tuner.Apply(profile);
        var knobs = new List<KnobResult>(result.Results);

        if (unlock)
        {
            knobs.Add(gpu.Tuner.ForceUnlock());
        }

        if (args?["fan"]?.GetValue<string>() is { } fan)
        {
            if (fan.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var rc = gpu.Tuner.RestoreAutoFansRaw();
                knobs.Add(rc == NvmlReturn.Success
                    ? KnobResult.Ok("fans", "auto")
                    : KnobResult.Fail("fans", rc.ToString()));
            }
            else if (uint.TryParse(fan, out uint duty))
            {
                uint normalized = TuningMath.NormalizeFixedFanDuty(duty, gpu.Tuner.Capabilities.FanMinDutyPct);
                var rc = gpu.Tuner.SetAllFansRaw(normalized);
                knobs.Add(rc == NvmlReturn.Success
                    ? KnobResult.Ok("fans", $"{normalized}% fixed")
                    : KnobResult.Fail("fans", rc.ToString()));
            }
        }

        return new
        {
            all_succeeded = knobs.All(k => k.Applied),
            results = knobs,
            now_applied = ReadState(gpu),
        };
    }

    private static object ReadState(GpuContext gpu)
    {
        var (core, mem, power, boost, lockMhz) = gpu.Tuner.ReadCurrent();
        return new
        {
            core_offset_mhz = core,
            mem_offset_mhz = mem,
            power_limit_w = power,
            voltage_boost_pct = boost,
            lock_clock_mhz = lockMhz,
        };
    }

    private static object RunStepper(GpuContext gpu, JsonObject? args)
    {
        var options = new StepperOptions
        {
            StepMHz = Math.Clamp(args?["step_mhz"]?.GetValue<int>() ?? 30, 5, 60),
            SecondsPerStep = Math.Clamp(args?["seconds_per_step"]?.GetValue<int>() ?? 60, 30, 300),
            MaxOffsetMHz = Math.Clamp(args?["max_offset_mhz"]?.GetValue<int>() ?? 300, 50, 600),
        };
        int maxMinutes = Math.Clamp(args?["max_minutes"]?.GetValue<int>() ?? 30, 5, 120);

        var stepper = new StabilityStepper(gpu.Tuner) { TargetPciBusId = gpu.PciBusId };
        var done = new ManualResetEventSlim(false);
        StepperStatus? final = null;
        stepper.StatusChanged += status =>
        {
            if (!status.Running)
            {
                final = status;
                done.Set();
            }
        };

        stepper.Start(options);
        if (!done.Wait(TimeSpan.FromMinutes(maxMinutes)))
        {
            // Time budget exhausted: cancel (the stepper restores the starting
            // offset on its cancel path) and give the restore a moment to land,
            // so a client that timed out and walked away never leaves an
            // untested offset applied.
            stepper.Cancel();
            bool restored = done.Wait(TimeSpan.FromSeconds(90));
            return new
            {
                all_succeeded = false,
                error = $"Stepping did not finish within max_minutes={maxMinutes}. " +
                        (restored
                            ? "The run was cancelled and the starting offset restored."
                            : "Cancellation was requested but not yet confirmed — verify the applied offset."),
                phase = final?.Phase ?? "timeout",
                log = final?.Log ?? [],
            };
        }

        return new
        {
            all_succeeded = final?.Phase == "done",
            phase = final?.Phase,
            stable_core_offset_mhz = final?.ResultOffsetMHz,
            log = final?.Log ?? [],
        };
    }

    private static object RunVramTest(GpuContext gpu, JsonObject? args)
    {
        int seconds = Math.Clamp(args?["seconds"]?.GetValue<int>() ?? 90, 15, 1800);

        using var vram = new VramTest { TargetPciBusId = gpu.PciBusId };
        var done = new ManualResetEventSlim(false);
        vram.ProgressChanged += progress =>
        {
            if (progress.State is not StressState.Running)
            {
                done.Set();
            }
        };

        double peakMemJunction = 0;
        vram.Start();
        var start = DateTime.UtcNow;
        while (!done.IsSet)
        {
            if (done.Wait(TimeSpan.FromMilliseconds(500)))
            {
                break;
            }

            var snapshot = gpu.Poller.Poll();
            peakMemJunction = Math.Max(peakMemJunction, snapshot.MemJunctionTempC ?? 0);

            var p = vram.Progress;
            double elapsed = (DateTime.UtcNow - start).TotalSeconds;
            if ((elapsed >= seconds && p.Rounds >= 1) || elapsed >= seconds * 3)
            {
                break;
            }
        }

        vram.StopAndWait(TimeSpan.FromSeconds(10));
        var final = vram.Progress;

        bool stable = final.State is StressState.Stopped or StressState.Running && final.Rounds >= 1;
        return new
        {
            stable,
            state = final.State.ToString(),
            covered_gib = final.PlannedBytes / (double)(1L << 30),
            full_rounds = final.Rounds,
            error_count = final.ErrorCount,
            seconds_run = final.Elapsed.TotalSeconds,
            peak_mem_junction_c = peakMemJunction,
            detail = final.Detail,
        };
    }

    private static object RunStress(GpuContext gpu, JsonObject? args)
    {
        int seconds = Math.Clamp(args?["seconds"]?.GetValue<int>() ?? 30, 5, 600);
        uint intensity = Math.Clamp(args?["intensity"]?.GetValue<uint>() ?? 4096, 512, 16384);
        var pattern = (args?["pattern"]?.GetValue<string>()?.ToUpperInvariant()) switch
        {
            "TRANSITIONS" or "TRANSITION" => StressPattern.Transitions,
            "EXCURSIONS" or "EXCURSION" or "BURSTS" or "DWELL" => StressPattern.BoostExcursions,
            _ => StressPattern.Sustained,
        };

        using var stress = new GpuStressTest
        {
            IterationsPerDispatch = intensity,
            Pattern = pattern,
            TargetPciBusId = gpu.PciBusId,
        };
        var done = new ManualResetEventSlim(false);
        StressProgress? final = null;
        double rateSum = 0;
        int rateSamples = 0;

        stress.ProgressChanged += progress =>
        {
            if (progress.State == StressState.Running && progress.DispatchesPerSecond > 0)
            {
                rateSum += progress.DispatchesPerSecond;
                rateSamples++;
            }

            if (progress.State is not StressState.Running)
            {
                final = progress;
                done.Set();
            }
        };

        uint peakTemp = 0;
        double peakHotOrMem = 0;
        double peakPower = 0;
        uint maxClock = 0;

        stress.Start();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline && !done.IsSet)
        {
            Thread.Sleep(500);
            var snapshot = gpu.Poller.Poll();
            peakTemp = Math.Max(peakTemp, snapshot.GpuTempC ?? 0);
            peakHotOrMem = Math.Max(peakHotOrMem, snapshot.MemJunctionTempC ?? 0);
            peakPower = Math.Max(peakPower, snapshot.PowerW ?? 0);
            maxClock = Math.Max(maxClock, snapshot.CoreClockMHz ?? 0);
        }

        stress.StopAndWait(TimeSpan.FromSeconds(10));
        final ??= stress.Progress;

        bool stable = final.State is StressState.Stopped or StressState.Running;
        return new
        {
            stable,
            state = final.State.ToString(),
            pattern = pattern.ToString(),
            transitions_verified = final.Transitions,
            seconds_run = final.Elapsed.TotalSeconds,
            error_count = final.ErrorCount,
            total_dispatches = final.TotalDispatches,
            avg_dispatches_per_second = rateSamples > 0 ? rateSum / rateSamples : 0,
            peak_gpu_temp_c = peakTemp,
            peak_mem_junction_c = peakHotOrMem,
            peak_power_w = peakPower,
            max_core_clock_mhz = maxClock,
            detail = final.Detail,
        };
    }

    private static bool AppServicesLikeElevationCheck()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static void WriteResult(JsonNode? id, JsonObject result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };
        Console.WriteLine(response.ToJsonString());
    }

    private static void WriteError(JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        Console.WriteLine(response.ToJsonString());
    }
}
