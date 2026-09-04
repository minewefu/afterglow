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
        if (args is not null && !CliGpu.TryParseIndex(args, out _, out string? gpuArgError))
        {
            // Never fall back to GPU 0 here: this binds every tuning write and
            // every stability verdict the agent will make.
            Console.Error.WriteLine(gpuArgError);
            return 2;
        }

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

            // Indexing a JsonArray or a JsonValue by name throws
            // InvalidOperationException, not JsonException — so a spec-legal
            // JSON-RPC batch (`[...]`), a non-string `method`, or a non-object
            // `params` used to terminate the whole server. That takes
            // reset_defaults down with it while tuning stays applied, and this
            // server ECHOES the client's requested protocolVersion, so it told a
            // conformant client it spoke a revision whose first batch message
            // killed it. Everything below is read defensively and any surprise
            // becomes one error response, never a dead process.
            var envelope = request as JsonObject;
            if (envelope is null)
            {
                WriteError(null, -32600, "Invalid request: expected a JSON-RPC object.");
                continue;
            }

            JsonNode? id = envelope.TryGetPropertyValue("id", out var idNode) ? idNode : null;
            string? method = AsString(envelope, "method");

            try
            {
                switch (method)
                {
                    case "initialize":
                        string protocol = AsString(envelope["params"] as JsonObject, "protocolVersion") ?? "2025-06-18";
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
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
            {
                // One malformed request is one error response. The server keeps
                // serving — an agent's abort channel must not be closed by its
                // own bad message.
                WriteError(id, -32600, $"Invalid request: {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Reads a string property without throwing when it is absent, null, or not
    /// a string. <c>node["x"]?.GetValue&lt;string&gt;()</c> throws on a number or
    /// an object, which is how a well-formed but wrongly-typed field could kill
    /// the server.
    /// </summary>
    private static string? AsString(JsonObject? node, string name) =>
        node is not null && node.TryGetPropertyValue(name, out var value) && value is JsonValue v
        && v.TryGetValue(out string? text)
            ? text
            : null;

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
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException
                                     or UnauthorizedAccessException or NotSupportedException
                                     or System.Text.Json.JsonException or FormatException
                                     or OverflowException)
        {
            // One tool call's failure is one error response. UnauthorizedAccess
            // from a profile write — an ordinary outcome under a locked-down
            // %ProgramData% — used to escape this filter and take the whole
            // stdio server down, closing the agent's abort channel with tuning
            // still applied.
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

        // Every tool that writes carries all_succeeded, and the isError probe
        // looks for exactly that. Returning only `results` made a wholly failed
        // reset — the driver refusing every knob after a TDR — arrive as a
        // successful tool call, so an agent recorded a clean abort and left the
        // GPU at the offset that had just crashed it. This is the tool the
        // description calls "The safe abort"; it was the one write tool without
        // the flag.
        static object ResetDefaults(Core.Hardware.GpuContext gpu)
        {
            var reset = gpu.Tuner.ResetToDefaults();
            return new { all_succeeded = reset.AllSucceeded, results = reset.Results };
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
                _ => gpu is null ? RequireGpu() : (object)PolledSnapshot(gpu)),

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
                _ => gpu is null ? RequireGpu() : ResetDefaults(gpu)),

            new ToolDef(
                "run_stress",
                "Run the bit-exact burn test while sampling telemetry. Returns pass/fail (computation errors " +
                "or a driver reset = current clocks unstable), dispatch throughput (a relative performance " +
                "score), and peak temperature/power/clock seen during the burn. This is the ground-truth " +
                "signal for an autonomous tuning loop: apply offset -> run_stress -> check state/errors -> step.",
                Schema(
                    ("seconds", "integer",
                        "Burn duration, 5-600 (default 30). The 'transitions' pattern completes its first " +
                        $"load/idle cycle only at ~{GpuStressTest.TransitionsFirstCycleSeconds} s and earns no verdict " +
                        "before that — give it 60 s or more.",
                        false),
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
                    var (core, mem, power, boost, _) = gpu.Tuner.ReadCurrent();
                    var profile = new TuningProfile
                    {
                        Name = name,
                        CoreOffsetMHz = core,
                        MemOffsetMHz = mem,
                        PowerLimitW = power > 0 ? power : null,
                        VoltageBoostPct = boost,
                        // The lock Afterglow applied, never the observed ceiling
                        // (see IGpuTuner.ReadCurrent): a profile that captured an
                        // Arc factory ceiling re-applied it as a clamp.
                        LockedCoreClockMHz = gpu.Tuner.AppliedLockMHz,

                        // Stamp the card these values were read from. Without it
                        // TuningProfile's own cross-card guard short-circuits
                        // (null matches anything), so a profile captured from one
                        // GPU applied silently to another.
                        GpuUuid = gpu.Uuid,
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
                : args?["lock_clock_mhz"]?.GetValue<uint>() ?? gpu.Tuner.AppliedLockMHz, // applied, never observed
        };

        // Schema validation errors cite generic sanity bounds; report the
        // ranges that actually matter — this GPU's — alongside them. Floors
        // come from the device where its driver reports lower ones than the
        // NVIDIA-era defaults (Intel clamps reach 100 MHz).
        var validationCaps = gpu.Tuner.Capabilities;
        uint lockFloor = validationCaps.LockClockMinMHz is > 0 and < 210 ? validationCaps.LockClockMinMHz : 210;
        double powerFloor = validationCaps is { SupportsPowerLimit: true, PowerLimitMinW: > 0 and < 50 }
            ? validationCaps.PowerLimitMinW
            : 50;
        if (profile.Validate(lockFloor, powerFloor) is string validationError)
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

        // Read the fan argument BEFORE any write lands. It was the only argument
        // parsed afterwards, so a JSON number (`"fan": 75` rather than `"75"`)
        // threw AFTER the clock and power knobs had already been applied, and the
        // caller got a bare error describing none of it.
        string? fanArg = null;
        if (args?["fan"] is { } fanNode)
        {
            if (fanNode is JsonValue fanValue && fanValue.TryGetValue(out string? fanText))
            {
                fanArg = fanText;
            }
            else
            {
                return new
                {
                    all_succeeded = false,
                    error = "'fan' must be a STRING: \"auto\" or a duty percentage 0-100 (e.g. \"75\"). " +
                            "Nothing was applied.",
                };
            }
        }

        // Release BEFORE applying, exactly as the CLI does. An explicit
        // unlock:true is a direct instruction, and Apply deliberately declines to
        // touch a clamp this session did not write — reporting that refusal as a
        // failed knob. Applying first therefore told the agent all_succeeded:false,
        // advising it to run a CLI command, in the same payload whose
        // now_applied.lock_clock_mhz reads null because the release then worked.
        // Releasing first also leaves nothing for Apply's lock-less path to find.
        var knobs = new List<KnobResult>();
        KnobResult? refusedRelease = null;
        if (unlock)
        {
            var explicitRelease = gpu.Tuner.ForceUnlock();
            if (explicitRelease.Applied)
            {
                knobs.Add(explicitRelease);
            }
            else
            {
                // A refused release leaves the clamp tracked, so Apply's
                // lock-less path retries it; its knob is the one verdict, and
                // it lands in the list below. Reporting both gave the agent a
                // failed knob beside a verified release of the same lock.
                refusedRelease = explicitRelease;
            }
        }

        var result = gpu.Tuner.Apply(profile);
        if (refusedRelease is { } refused && !result.Results.Any(k => k.Knob == "clock lock"))
        {
            knobs.Add(refused); // Apply found nothing to retry: the refusal stands, so all_succeeded reflects it
        }

        foreach (var knob in result.Results)
        {
            // With the release already done and reported, Apply's leftover
            // "nothing else in this profile applies" note is noise that would
            // read as though the unlock had not happened.
            if (unlock && knob.Applied && knob.Knob == "profile")
            {
                continue;
            }

            knobs.Add(knob);
        }

        if (fanArg is { } fan)
        {
            if (fan.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var rc = gpu.Tuner.RestoreAutoFansRaw();
                knobs.Add(rc == NvmlReturn.Success
                    ? KnobResult.Ok("fans", "auto")
                    : KnobResult.Fail("fans", rc.ToString()));
            }
            else if (uint.TryParse(fan, out uint duty) && duty <= 100)
            {
                uint normalized = TuningMath.NormalizeFixedFanDuty(duty, gpu.Tuner.Capabilities.FanMinDutyPct);
                var rc = gpu.Tuner.SetAllFansRaw(normalized);
                knobs.Add(rc == NvmlReturn.Success
                    ? KnobResult.Ok("fans", $"{normalized}% fixed")
                    : KnobResult.Fail("fans", rc.ToString()));
            }
            else
            {
                // Without this, {"fan":"75%"} issued no command, added no result,
                // and left all_succeeded vacuously true with no fan field in
                // now_applied to contradict it — an agent believed it had pinned
                // the fans. The CLI twin already refuses this.
                knobs.Add(KnobResult.Fail(
                    "fans", $"'{fan}' is not \"auto\" or a duty percentage 0-100 — the fans were not touched"));
            }
        }

        return new
        {
            all_succeeded = knobs.All(k => k.Applied),
            results = knobs,
            now_applied = ReadState(gpu),
        };
    }

    /// <summary>
    /// Running peak that stays null until the sensor actually answers, so a
    /// sensor the device does not expose is reported as absent rather than as a
    /// measured zero.
    /// </summary>
    private static T? Peak<T>(T? running, T? sample)
        where T : struct, IComparable<T> =>
        sample is not { } value ? running
            : running is not { } current ? value
            : value.CompareTo(current) > 0 ? value : current;

    /// <summary>
    /// One telemetry snapshot, primed where the vendor needs it.
    /// <para>
    /// Intel derives board power and utilization from monotonic counters, so
    /// those values only exist as a delta between two reads. An agent calling
    /// get_telemetry once — the normal case — got null for both every time,
    /// making the tool look like the GPU reports no power or load at all.
    /// The CLI's own --once/--json path already primes for exactly this reason;
    /// the MCP surface did not. NVIDIA polls once, immediately, as before.
    /// </para>
    /// </summary>
    private static Afterglow.Core.Telemetry.GpuSnapshot PolledSnapshot(GpuContext gpu)
    {
        if (gpu.Vendor != GpuVendor.Intel)
        {
            return gpu.Poller.Poll();
        }

        // The source keeps its last counter pair across calls (the delta stays
        // valid for 30 s), so after the first call — or any call within 30 s of
        // the stress/VRAM loops' own polling — the first read already carries
        // power and load. Re-priming unconditionally blocked this
        // single-threaded server for 150 ms and cost a second IGCL read on every
        // call; now only a read that comes back unprimed pays for it.
        var first = gpu.Poller.Poll();
        if (first.PowerW is not null || first.GpuUtilPct is not null)
        {
            return first;
        }

        Thread.Sleep(150);
        return gpu.Poller.Poll();
    }

    private static object ReadState(GpuContext gpu)
    {
        var (core, mem, power, boost, lockMhz) = gpu.Tuner.ReadCurrent();
        var caps = gpu.Tuner.Capabilities;

        // Null, not 0, for a knob the device does not expose — an agent reading
        // "core_offset_mhz: 0" would take it as a real, settable current value.
        // NVIDIA supports both offsets, so its payload is unchanged.
        return new
        {
            core_offset_mhz = caps.SupportsCoreOffset ? core : (int?)null,
            mem_offset_mhz = caps.SupportsMemOffset ? mem : (int?)null,
            power_limit_w = power,
            voltage_boost_pct = boost,
            lock_clock_mhz = lockMhz,
        };
    }

    private static object RunStepper(GpuContext gpu, JsonObject? args)
    {
        // The stepper searches the core-clock offset. On a GPU with no such knob
        // the search space is empty, and it used to run a full burn at offset 0
        // and hand the agent back a "stable offset" for a control that does not
        // exist. Say so instead of manufacturing a verdict.
        if (!gpu.Tuner.Capabilities.SupportsCoreOffset)
        {
            return new
            {
                error = $"{gpu.Name} exposes no core-clock offset, so there is no offset to search. " +
                    "find_stable_offset applies only to GPUs whose driver reports a core-offset knob; " +
                    "use run_stress to validate this GPU's current settings instead.",
            };
        }

        var options = new StepperOptions
        {
            StepMHz = Math.Clamp(args?["step_mhz"]?.GetValue<int>() ?? 30, 5, 60),
            SecondsPerStep = Math.Clamp(args?["seconds_per_step"]?.GetValue<int>() ?? 60, 30, 300),
            MaxOffsetMHz = Math.Clamp(args?["max_offset_mhz"]?.GetValue<int>() ?? 300, 50, 600),
        };
        int maxMinutes = Math.Clamp(args?["max_minutes"]?.GetValue<int>() ?? 30, 5, 120);

        var stepper = new StabilityStepper(gpu.Tuner) { TargetPciBusId = gpu.PciBusId, TargetVendorId = gpu.PciVendorId };
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

            // Two separate questions. `unwound` says the worker thread finished;
            // `StartOffsetRestored` says the restore WRITE succeeded. Reporting
            // the first as the second told an agent the card was back at its
            // starting offset whenever the thread merely ended — including when
            // ApplyOffset had exhausted its three retries ~85 s earlier and given
            // up with an untested overclock still applied.
            bool unwound = done.Wait(TimeSpan.FromSeconds(90));
            bool? restored = unwound ? final?.StartOffsetRestored : null;
            return new
            {
                all_succeeded = false,
                error = $"Stepping did not finish within max_minutes={maxMinutes}. " +
                        (restored switch
                        {
                            true => "The run was cancelled and the starting offset restored.",
                            false => "The run was cancelled but the starting offset could NOT be restored — " +
                                     "this GPU may still be at the offset under test. Call reset_defaults.",
                            null => "Cancellation was requested but not yet confirmed — verify the applied offset.",
                        }),
                start_offset_restored = restored,
                phase = final?.Phase ?? "timeout",
                log = final?.Log ?? [],
            };
        }

        // The restore matters on EVERY exit, not only the timeout: the sweep's
        // hardware-failure paths end here, and a run whose restore failed has
        // left the card at the offset that just miscalculated or reset the
        // driver. Reporting only phase/all_succeeded hid that completely.
        return new
        {
            all_succeeded = final?.Phase == "done" && final?.StartOffsetRestored != false,
            phase = final?.Phase,
            stable_core_offset_mhz = final?.ResultOffsetMHz,

            // At "done" the stepper's flag reports the CLOSING re-write of the
            // result offset, not a restore of the start offset (the card is
            // meant to stay at the result); the two must not share a field an
            // agent reads as "still at an untested offset — reset it".
            start_offset_restored = final?.Phase == "done" ? null : final?.StartOffsetRestored,
            result_offset_confirmed = final?.Phase == "done" ? final?.StartOffsetRestored : null,
            applied_core_offset_mhz = final?.CurrentOffsetMHz,
            log = final?.Log ?? [],
        };
    }

    private static object RunVramTest(GpuContext gpu, JsonObject? args)
    {
        int seconds = Math.Clamp(args?["seconds"]?.GetValue<int>() ?? 90, 15, 1800);

        using var vram = new VramTest { TargetPciBusId = gpu.PciBusId, TargetVendorId = gpu.PciVendorId };
        var done = new ManualResetEventSlim(false);
        vram.ProgressChanged += progress =>
        {
            if (progress.State is not StressState.Running)
            {
                done.Set();
            }
        };

        double? peakMemJunction = null;
        vram.Start();
        var start = DateTime.UtcNow;
        while (!done.IsSet)
        {
            if (done.Wait(TimeSpan.FromMilliseconds(500)))
            {
                break;
            }

            var snapshot = gpu.Poller.Poll();
            peakMemJunction = Peak(peakMemJunction, snapshot.MemJunctionTempC);

            var p = vram.Progress;
            double elapsed = (DateTime.UtcNow - start).TotalSeconds;
            if ((elapsed >= seconds && p.Rounds >= 1) || elapsed >= seconds * 3)
            {
                break;
            }
        }

        // An abandoned run leaves a stale mid-run snapshot behind. Reporting
        // stable=true from it would tell the agent the card passed a test that
        // never actually finished, so say inconclusive instead.
        bool stoppedCleanly = vram.StopAndWait(TimeSpan.FromSeconds(30));
        var final = vram.Progress;

        // Same rule as the burn: "did not finish" and "did no work" are both
        // absence of a verdict, not evidence of instability.
        // Rounds only increment after a fully clean sweep, so on a detected
        // miscompare the count is still 0 — using it alone as the work signal
        // reported found corruption as "nothing was actually verified".
        // Same split as the burn: keep the engine's message, but only a detected
        // miscompare or device reset counts as a RESULT.
        bool hasEngineDetail = TerminalFailure(final.State);
        bool hardwareVerdict = final.IsHardwareVerdict;
        bool didRealWork = final.Rounds >= 1 || final.ErrorCount > 0;
        bool stable = stoppedCleanly && final.State is StressState.Stopped && didRealWork;
        bool noVerdict = !hardwareVerdict
            && (!stoppedCleanly || !didRealWork || final.State == StressState.Failed);
        return new
        {
            stable,
            inconclusive = noVerdict,
            state = final.State.ToString(),

            // Same as the burn: an engine that could not run is an error to
            // the envelope's isError, not a normal result.
            error = final.State == StressState.Failed
                ? final.Detail ?? "the VRAM test engine could not run"
                : null,
            covered_gib = final.PlannedBytes / (double)(1L << 30),
            full_rounds = final.Rounds,
            error_count = final.ErrorCount,
            seconds_run = final.Elapsed.TotalSeconds,
            peak_mem_junction_c = peakMemJunction,
            detail = hasEngineDetail ? final.Detail
                : !stoppedCleanly
                ? "The VRAM test did not stop within 30 s — these figures are a stale mid-run snapshot, " +
                  "not a completed run, and no stability conclusion can be drawn from them."
                : !didRealWork
                    ? "The VRAM test completed no full coverage round, so nothing was actually verified. " +
                      "Retry with more seconds."
                    : final.Detail,
        };
    }

    private static object RunStress(GpuContext gpu, JsonObject? args)
    {
        int seconds = Math.Clamp(args?["seconds"]?.GetValue<int>() ?? 30, 5, 600);
        uint intensity = Math.Clamp(args?["intensity"]?.GetValue<uint>() ?? 4096, 512, 16384);
        // An unrecognised pattern is refused, not quietly replaced with the
        // sustained burn. This is the unattended surface: substituting a regime
        // the caller did not ask for and then returning stable:true is how a
        // typo becomes a stability verdict for a test that never ran. The CLI
        // was fixed for exactly this; its twin here was not. Note there was no
        // "SUSTAINED" case at all, so the code could not tell "asked for
        // sustained" from "could not parse".
        string? patternArg = args?["pattern"]?.GetValue<string>();
        var pattern = patternArg?.ToUpperInvariant() switch
        {
            "TRANSITIONS" or "TRANSITION" => StressPattern.Transitions,
            "EXCURSIONS" or "EXCURSION" or "BURSTS" or "DWELL" => StressPattern.BoostExcursions,
            "SUSTAINED" or null => StressPattern.Sustained,
            _ => (StressPattern?)null,
        };

        if (pattern is not { } chosenPattern)
        {
            return NothingRan(
                $"Unknown pattern '{patternArg}' — expected sustained, transitions or excursions. " +
                "Nothing was run: substituting a different burn would attach a stability verdict to a " +
                "test you did not ask for.");
        }

        // The same floor the CLI enforces up front. Burning a too-short
        // transitions run to report "inconclusive" loaded the GPU for nothing
        // and told the agent the floor only after it had paid for it.
        if (GpuStressTest.SecondsShortfall(chosenPattern, seconds) is string tooShort)
        {
            return NothingRan($"Nothing was run: {tooShort}.");
        }

        using var stress = new GpuStressTest
        {
            IterationsPerDispatch = intensity,
            Pattern = chosenPattern,
            TargetPciBusId = gpu.PciBusId,
            TargetVendorId = gpu.PciVendorId,
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

        // Peaks stay null until a sensor actually reads. Folding a missing sensor
        // in as 0 reported "peak_gpu_temp_c: 0" to the agent for a device with no
        // temperature sensor at all — a measurement that was never taken, and one
        // an agent would reasonably read as "ice cold, push it harder".
        uint? peakTemp = null;
        double? peakHotOrMem = null;
        double? peakPower = null;
        uint? maxClock = null;

        stress.Start();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline && !done.IsSet)
        {
            Thread.Sleep(500);
            var snapshot = gpu.Poller.Poll();
            peakTemp = Peak(peakTemp, snapshot.GpuTempC);
            peakHotOrMem = Peak(peakHotOrMem, snapshot.MemJunctionTempC);
            peakPower = Peak(peakPower, snapshot.PowerW);
            maxClock = Peak(maxClock, snapshot.CoreClockMHz);
        }

        // A burn that never acknowledged its stop was abandoned: its counters are
        // a stale mid-run snapshot and nothing was verified after them. Telling
        // an agent stable=true from that is exactly the false pass this tool
        // must never produce, so it is reported as inconclusive instead. A run
        // that did no verified work at all is likewise not evidence of anything.
        bool stoppedCleanly = stress.StopAndWait(TimeSpan.FromSeconds(30));
        final ??= stress.Progress;

        // Two different ways to have no verdict, and BOTH must set inconclusive.
        // Gating only `stable` on the work count while leaving `inconclusive`
        // keyed to the stop turned a run that tested nothing into a definitive
        // "unstable" — with inconclusive:false and detail:null, the agent had no
        // field distinguishing "nothing was tested" from "the GPU miscomputed",
        // and the tool description tells it the latter.
        bool didRealWork = final.VerdictGap is null;
        bool stable = stoppedCleanly && final.IsCleanPass;

        // Two predicates, not one. `hasEngineDetail` decides whether the
        // engine's own message survives — a TDR's diagnosis must never be
        // replaced by advice to re-run the offset that caused it. `IsHardwareVerdict`
        // decides whether this is a RESULT: only a detected artifact or a device
        // reset is. The engine also reports Failed when it could not run at all
        // (no adapter, no D3D device) or gave up waiting, and folding those into
        // "a result" told the agent "not stable, not inconclusive" about a run
        // that tested nothing — while the give-up path's own text says
        // "the run is inconclusive".
        bool hasEngineDetail = TerminalFailure(final.State);
        bool noVerdict = !final.IsHardwareVerdict
            && (!stoppedCleanly || !didRealWork || final.State == StressState.Failed);
        return new
        {
            stable,
            inconclusive = noVerdict,
            state = final.State.ToString(),

            // An engine that could not run at all (no adapter, a refused guess,
            // no D3D device) is an error to the MCP envelope, not a normal
            // result: the dispatcher's isError reads this field, and an agent
            // gating on the standard flag was stepping its loop on a burn that
            // never happened.
            error = final.State == StressState.Failed
                ? final.Detail ?? "the burn engine could not run"
                : null,
            pattern = pattern.ToString(),
            transitions_verified = final.Transitions,
            seconds_run = final.Elapsed.TotalSeconds,
            error_count = final.ErrorCount,
            total_dispatches = final.TotalDispatches,
            burn_dispatches = final.BurnDispatches,
            avg_dispatches_per_second = rateSamples > 0 ? rateSum / rateSamples : 0,
            peak_gpu_temp_c = peakTemp,
            peak_mem_junction_c = peakHotOrMem,
            peak_power_w = peakPower,
            max_core_clock_mhz = maxClock,
            detail = hasEngineDetail ? final.Detail
                : !stoppedCleanly
                    ? "The burn did not stop within 30 s — these figures are a stale mid-run snapshot, " +
                      "not a completed run, and no stability conclusion can be drawn from them."
                    : final.VerdictGap is { } gap
                        ? $"{char.ToUpperInvariant(gap[0])}{gap[1..]}. Retry with more seconds or a lower intensity."
                        : final.Detail,
        };
    }

    /// <summary>
    /// States that carry the engine's own explanation, which must never be
    /// replaced by a generic one. Note this is NOT the same as a hardware
    /// verdict: <see cref="StressState.Failed"/> means the run never got going
    /// (a refused adapter, no D3D device) and says nothing about the GPU, while
    /// the other two are the hardware failing under a load that did run. See
    /// <c>StressProgress.IsHardwareVerdict</c> for that distinction.
    /// </summary>

    /// <summary>
    /// The one shape for "nothing was run": an agent reading `stable` or
    /// `inconclusive` must never find them absent and treat the gap as a
    /// verdict. Two hand-built copies of this object had already been the
    /// reason one of them lacked those fields.
    /// </summary>
    private static object NothingRan(string error) =>
        new { all_succeeded = false, stable = false, inconclusive = true, error };

    private static bool TerminalFailure(StressState state) =>
        state is StressState.ArtifactDetected or StressState.DeviceLost or StressState.Failed;

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
