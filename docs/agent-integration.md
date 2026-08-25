# Driving Afterglow with an AI agent

Afterglow is agent-native: an AI agent can monitor the GPU, apply tuning, and validate
stability autonomously — including running a closed tuning loop that finds the best stable
clocks. Two integration surfaces exist; both inherit the full safety architecture (every
write is clamped to the driver-reported legal range, offsets/power/voltage are
readback-verified, and the burn test reports bit-exact computation errors and driver
resets), so an agent physically cannot request values the driver considers illegal.

## 1. MCP server (recommended)

```bash
afterglow-cli mcp
```

Speaks Model Context Protocol over stdio (newline-delimited JSON-RPC). Register it with any
MCP-capable agent host. **Run the host/server elevated for write access** — unelevated, all
read tools work and write tools report `needs administrator rights` per knob.

Example client registration (Claude Code):

```bash
claude mcp add afterglow -- "C:\Program Files\Afterglow\afterglow-cli.exe" mcp
```

Tools:

| Tool | What it does |
|---|---|
| `get_capabilities` | GPU identity + driver-reported legal ranges (the agent's action space) |
| `get_telemetry` | Full sensor snapshot: clocks, temps (incl. memory junction), instantaneous power, fans, throttle reasons, throttle headroom |
| `apply_tuning` | Apply core/mem offsets, power limit, clock lock (undervolt), voltage boost, fan duty — clamped + verified, per-knob results |
| `reset_defaults` | The safe abort: everything back to driver defaults |
| `run_stress` | Error-checked burn for N seconds; returns `stable` (bit-exact + no TDR), dispatch throughput (relative perf score), and peak temp/power/clock during the burn |
| `list_profiles` / `save_profile` / `apply_profile` | Persist and reuse results |

### The autonomous tuning loop

`run_stress` is the ground-truth oracle that closed-source auto-tuners lack. A robust
agent loop:

1. `get_capabilities` → learn the legal offset range and power limits.
2. `get_telemetry` → baseline temps/power.
3. Loop: `apply_tuning {core_offset_mhz: X}` → `run_stress {seconds: 60}` →
   - `stable: true` → record X and the `avg_dispatches_per_second` (performance actually
     improved? clocks can plateau), step X up;
   - `stable: false` (computation errors or driver reset) → step back and confirm with a
     longer burn.
4. On the confirmed offset: `save_profile {name: "agent-tuned"}`.
5. Anything unexpected → `reset_defaults`.

Watch `peak_gpu_temp_c` / `peak_power_w` between steps and stop early on thermal ceilings —
the same signal a human tuner uses. Afterglow's TDR watchdog remains armed the whole time,
and a human can always pull the plug with Ctrl+Alt+R.

Note: the in-app Stability page's guided stepper implements this exact loop natively if you
just want the result without an agent.

## 2. Plain CLI with `--json`

For agents that drive a shell instead of MCP:

```bash
afterglow-cli caps --json          # capabilities/ranges
afterglow-cli monitor --once --json# one telemetry snapshot per GPU
afterglow-cli get --json           # currently applied values
afterglow-cli set --core-offset 150 --mem-offset 500   # human-readable per-knob results, exit code 0/1
afterglow-cli stress --seconds 60  # exit code 0 = stable, 1 = errors/reset
afterglow-cli reset
```

## Safety notes for agent authors

- Ranges come from the driver for the exact GPU; `apply_tuning` clamps and reports clamping.
- `run_stress` failing means the *current clocks* are unstable — always step back before
  retrying, and prefer a longer confirmation burn before accepting a result.
- Memory offsets raise bandwidth but errors often appear only at high working-set sizes;
  keep the default 512 MiB working set or larger when validating memory clocks.
- Leave `reset_defaults` in every error path. It is idempotent and always safe.
