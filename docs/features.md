# Afterglow features in detail

The [README](../README.md) gives the overview; this page is the full reference — every
feature, with the method behind it and where it has been verified.

## Tuning

- Core & memory clock offsets with driver-reported legal ranges (RTX 5090: −1000…+1000 core,
  −2000…+6000 memory), clamped and readback-verified
- Power limit (watts and %), voltage boost where the hardware exposes it
- **Clock-lock undervolting** — the documented-API method that works on RTX 50: cap the boost
  clock while a positive offset shifts the V/F curve, hitting the same clock at lower voltage.
  One-click presets in the undervolt wizard
- **Per-point V/F curve editor** — direct edits to the driver's stored curve table, the
  mechanism Afterburner's curve editor uses: per-point offsets, a one-click flatten undervolt
  (raise the point at your target voltage, cap everything above it — and any lower point that
  stock already runs faster than the target), and clear-all. Out-of-range offsets are refused,
  never clamped, and every write — clear-all included — is checked by reading the table back
  against what you asked for (when a readback isn't possible, the result says so instead of
  claiming verification). Verified live on RTX 5090; expected on RTX 20/30/40 via the same
  interfaces. Also `afterglow-cli vfpoints`
- **Measured V/F curve** — the stored table says what the driver intends; the measured curve
  shows what the GPU actually does under the power limit and thermals. A ~1-minute probe locks
  each clock step under load and records the voltage the driver selects (plus continuous
  passive recording while you game). Pick any
  point on the measured curve and Afterglow computes the exact offset + lock that holds that
  clock at that voltage

- Knob-by-knob apply results: every slider reports applied / clamped / failed individually

## Fans

- Interactive curve editor (drag, double-click to add, right-click to remove points)
- Fast-up / damped-down **hysteresis**, configurable **zero-RPM** window with restart
  hysteresis, ramp limiting, hardware minimum-spin handling
- Temperature source selection: GPU core, hot spot, or **memory junction**
- True 0% (fan stop) through the modern per-fan driver interface, with per-fan RPM readouts

## Monitoring

- Clocks, temperatures (core + **memory junction on RTX 50** — the sensor most tools still
  can't show there), board power (instantaneous via the driver's field API where exposed,
  with the 1-second average alongside), core voltage, utilization, VRAM, per-fan duty & RPM,
  PCIe link/throughput, perf state
- **Plain-language throttle analysis**: instead of "PerfCap: SwPowerCap" you get "Power limit —
  the board hit its power limit and lowered clocks", plus a live **"°C of headroom to
  throttle"** readout straight from the driver
- History graphs, session stats, HWiNFO-style CSV logging with rotation
- Temperature alerts to the system tray

## FPS & frametimes (no RivaTuner needed)

- Per-game FPS via ETW present tracing: average, P1/P0.1 percentiles **and** Gamers
  Nexus-style 1%/0.1% lows — each metric labeled with its exact method
- Frametime graph, present-mode display, automatic foreground-game detection (UWP included)
- Click-through overlay for windowed/borderless/fullscreen-optimized games (the dominant
  modes today; legacy exclusive-fullscreen is documented as not covered — no injection, by design)

## Stability lab

- Built-in **burn test with bit-exact error detection**: a deterministic compute workload whose
  full output is re-verified byte-for-byte through a rotating window (a 256 KiB slice every
  ~2 s, covering the entire output each cycle) — a single flipped bit reports "unstable"
  instead of hoping you notice artifacts. Driver resets (TDR) during load are caught and reported
- **Stress patterns for the crashes other tools can't catch**: *Transition cycling* forces
  P-state/memory-clock switches and re-verifies VRAM retention across every transition
  (memory offsets that pass sustained burns routinely fail exactly there), and *Boost
  excursions* rides the boost overshoot through the top clock bins — the bursty desktop
  regime a power-limited burn never reaches
- **Crash forensics**: an always-on flight recorder keeps recent telemetry on disk; after a
  hard crash, the next launch correlates its final minutes with the Windows event log and
  explains the failure in plain language ("hard reset 3 min after load ended with +2000 MHz
  memory — matches transition instability"), instead of leaving you guessing
- **Full-VRAM test**: fills as much of the card's memory as the OS safely allows (29 GiB on a
  32 GiB card) with deterministic patterns, alternate rounds bit-inverted, verified on the GPU
  itself — 87 GiB/s measured over a 2-minute run on RTX 5090 with driver 616.56 (throughput
  varies by driver; the tool prints its own live rate) — catches memory-offset errors the
  bandwidth burn can't
- **Profile certification**: one click applies a saved profile and runs all four modes against
  it in sequence; each pass is stamped into the profile, pinned to the exact offsets tested
  (editing them invalidates the stamps), and passing all four marks it stable. A failure stops
  the run and resets the GPU to driver defaults
- **Guided stability stepper**: walks your core offset up step by step, burn-testing each,
  backs off on the first failure and runs a confirmation pass — an open-method alternative
  to closed OC scanners


## Agent-native (AI integration)

- `afterglow-cli mcp` runs a **Model Context Protocol server**, so AI agents can monitor,
  tune, and stability-test the GPU with typed tools — including `find_stable_offset`, the
  guided stepper as a single autonomous call, plus the primitives (apply → stress → verify)
  for building custom tuning loops on the error-checked burn test as ground truth
- `--json` output on the read commands for shell-driving agents
- Same safety envelope as the UI: agents can only apply driver-validated values, and every
  result is reported truthfully — see [docs/agent-integration.md](agent-integration.md)

## Automation & safety

- **Per-game auto profiles**: apply a profile when a game launches, restore your previous
  tuning when it exits (Afterburner has never had this)
- **Per-game driver settings without opening NVIDIA Control Panel**: frame-rate cap, vsync,
  and low latency per exe, written to the driver's own settings store — persistent, no
  injection, active even when Afterglow isn't running (this replaces RTSS's frame limiter)
- **Automation rules**: "memory junction ≥ 94 °C for 30 s → pin fans to 90%" — sustained-condition
  watchdogs that apply a profile, fix the fans, or reset to defaults, with cooldown and alerts
- **Session history**: every FPS capture is recorded with the offsets that were applied —
  before/after comparisons per game, exportable as Markdown
- Global hotkeys: overlay toggle, profile slots, and a **panic key** (Ctrl+Alt+R) that resets
  everything to driver defaults. All can be changed or turned off in Settings, and none is
  ever registered while it would type a character on one of your keyboard layouts (AltGr
  is Ctrl+Alt to Windows); a default that would falls back to Ctrl+Alt+F10/F11/F1–F5
  ([why](research/keyboard-layouts.md))
- **TDR watchdog**: if Windows logs a display-driver reset while your tuning is applied,
  Afterglow resets to defaults and tells you the overclock is suspect
- Crash recovery: an unclean shutdown with tuning applied is detected at the next start
- Unlimited named profiles as portable JSON; scriptable CLI (`afterglow-cli`) for automation
- Tray mode, start-with-Windows, start-minimized

## Intel Arc (beta)

One Afterglow, two vendors: since 1.3.0-beta, the app also drives Intel GPUs through
Intel's documented userland stacks — **IGCL** (the Intel Graphics Control Library, the
API Arc Control was built on) for control and telemetry, with **Level Zero Sysman** as a
second read-only source. Every capability is probed live on your exact device before the
UI exposes it; docs/research/intel-driver-apis.md records each interface, its source,
and what was verified on real hardware (an Arc B390 iGPU in a OneXPlayer 3 handheld).

Working on the verified device: live monitoring (clocks, watts from the driver's energy
counters, utilization, shared-memory budget, energy since driver load), the **frequency clamp**
as a verified write path (IGCL has a real readback getter, so clamp applies and releases
report "(verified)" — something NVML's lock cannot do), the full stability lab (burn
test, transition/excursion patterns, and a VRAM test that detects unified memory and
honestly tests the shared budget instead of pretending dedicated VRAM exists), profiles
with certification, FPS capture, and the multi-vendor `selftest`. Honestly absent *on
that device* (each probed, not assumed): temperature sensors, fan control (EC-owned on
handhelds), power-limit writes (no documented userland path answers; the clamp is the
supported lever and the package budget is shared with the CPU), offsets/voltage/V-F
control. A power-limit write path is implemented for hardware whose driver reports the
knob — expected on discrete Arc, awaiting field confirmation. **Discrete Arc owners
(B-series especially): run `afterglow-cli selftest` and paste the output into a GitHub
issue** — your driver will answer differently than the verified iGPU's.
