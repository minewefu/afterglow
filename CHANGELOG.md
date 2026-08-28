# Changelog

## 1.1.0 — 2026-08-28

- Independent defect review (docs/REVIEW-2026-08-28.md): all 22 findings and
  4 README-claim gaps addressed. Highlights: offset apply no longer claims
  "(verified)" unless the readback actually happened and matched; unknown
  driver throttle bits are surfaced raw instead of hidden (616.xx reports
  0x400); burn/VRAM tests bind explicitly to the NVIDIA adapter instead of
  trusting DXGI adapter order; burn verification now rotates across the
  entire output instead of a fixed slice; the V/F undervolt planner refuses
  unplannable targets, validates persisted curve data, and requires
  well-populated bins before offering a hardware write; the V/F probe
  restores range locks as range locks; the stepper ships as the
  `find_stable_offset` MCP tool and MCP `isError` now agrees with the result
  body; applied-state is stamped with the GPU UUID; plus tray-alert thread
  marshaling, fan-command failure surfacing, and interop cleanup

- Stress patterns: alongside the sustained burn, **Transition cycling** forces
  P-state and memory-clock switches with bit-exact VRAM retention checks across
  every transition, and **Boost excursions** rides the boost overshoot through
  the top clock bins — the two regimes where daily overclocks fail after
  passing every conventional stress test (`--pattern` in the CLI, `pattern`
  on the MCP `run_stress` tool)
- Crash forensics: an always-on flight recorder keeps recent telemetry and
  applied-offset markers on disk; after a hard crash, the next launch
  correlates the final minutes with the Windows event log (Kernel-Power 41,
  unexpected shutdown, WHEA, TDR, nvlddmkm) and explains the failure in plain
  language — banner on launch, full report on the Stability page
- Full-VRAM test: fills the card's memory up to the DXGI budget with
  deterministic patterns (alternate rounds bit-inverted) and verifies every
  element on the GPU — Stability page, `afterglow-cli vram`, and the MCP
  `run_vram_test` tool
- Profile certification: applies a saved profile and runs all four stability
  modes against it in sequence, stamping each pass into the profile pinned to
  the tested offsets; all four passes mark it stable, a failure resets the
  GPU to driver defaults — Profiles page (with per-mode badges and a
  CERTIFIED chip) and `afterglow-cli certify`
- Per-game NVIDIA driver settings: game rules can now set the driver's own
  frame-rate limiter, vsync, and low-latency (pre-rendered frames = 1) for
  the exe — written to the same DRS store the NVIDIA Control Panel edits,
  verified by readback, persistent with no injection and nothing resident;
  also `afterglow-cli drs`. Current drivers reject creating brand-new app
  profiles via NVAPI, so unknown exes need one-time registration in NVCP
  (games the driver already knows — effectively all of them — just work)
- Automation rules: "if GPU temp / memory junction / board power stays at or
  above X for Y seconds → apply a profile, pin the fans, or reset to
  defaults", with a 5-minute re-arm cooldown, tray notifications, and
  flight-recorder markers (Settings page)
- Session history: every FPS capture of 30 s or more is recorded with the
  offsets that were applied — before/after tuning comparisons per game on the
  FPS page, exportable as a Markdown table
- The flight recorder no longer blocks a second Afterglow instance from
  starting (screenshot/demo runs skip it; failures degrade to
  monitoring-without-black-box instead of an error dialog)
- Certifications are now pinned to the NVIDIA driver version they were earned
  on as well as the offsets — a driver update marks them "⚠ driver changed"
  (re-certify to confirm the tune still holds on the new driver's clock
  management). Certifications from older Afterglow builds carry no driver
  stamp and stay valid.
- Session comparison: Ctrl+click two recorded sessions on the FPS page for an
  A/B delta — avg FPS, 1% lows, board power, temperatures, and FPS-per-watt,
  each as newer-minus-older with percentages — copyable as a Markdown table.
  Comparing different applications is flagged as not comparable instead of
  silently diffed.
- Opt-in update check (off by default): one anonymous request to the GitHub
  Releases API at startup, a tray note if a newer version exists, and a
  "Check now" button in Settings → About. Nothing is downloaded or uploaded;
  failures are silent.
- Graph inspection: every large dashboard graph shows a crosshair readout on
  hover — the exact value under the mouse and how long ago it was sampled
  (real snapshot timestamps, not an assumed polling rate); the temperatures
  panel is one dual-series graph so a single hover reads GPU and memory
  junction together; the V/F curve reads out the nearest measured bin (clock,
  voltage, samples) on hover
- Click any dashboard hero tile to expand it into a full-width hoverable
  10-minute graph — VRAM, fans, and core voltage gain large views for the
  first time
- The automation-rules row in Settings no longer overflows: it wraps, the
  numeric fields fit their values, and the fan-% / profile inputs appear only
  for the action that uses them
- Review round 2 (docs/REVIEW-2026-08-28.md): fan commands are serialized and
  generation-checked so a mode change can never be undone by a slower
  in-flight command from the previous mode; the MCP server survives tools
  returning non-object JSON; `find_stable_offset` takes a `max_minutes`
  budget and restores the starting offset on timeout; the VRAM bandwidth
  claim re-measured honestly (87 GiB/s over 2 minutes on driver 616.56, not
  the 100+ recorded on 610.88)

## 1.0.2 — 2026-08-26

- Start with Windows now runs through a Task Scheduler entry that launches
  Afterglow elevated with no UAC prompt at logon: new Settings toggle, the
  installer checkbox uses the same mechanism, uninstall removes the task, and
  upgrades clean up the old Run-key autostart (which prompted every boot)

## 1.0.1 — 2026-08-26

- V/F chart: axis unit no longer collides with the last tick label; the
  live "now" marker and target label render on backing pills with
  edge-aware placement
- Screenshot mode accepts `--screenshot-delay N` for longer telemetry
  accumulation
- README screenshots replaced with real RTX 5090 captures

## 1.0.0 — 2026-08-26

Initial release.

- V/F Curve page: the GPU's real voltage/frequency map, measured — a ~1-minute
  active probe (lock each clock step under load, record the selected voltage)
  plus continuous passive recording; pick a point to compute the exact
  offset + clock-lock undervolt. Works on RTX 50, where NVIDIA blocks the
  curve interfaces.
- Agent-native: `afterglow-cli mcp` (Model Context Protocol server) and
  `--json` CLI output for autonomous tuning loops.
- Burn test loads FP32 pipes, INT pipe, and the memory controller
  (~95% of TGP measured on RTX 5090), with live sensors on the page.

> Note for anyone who ran pre-release development builds: profiles saved by those
> builds stored a placeholder fan-curve sentinel; re-save your profiles once (they
> now capture your real fan configuration).

- Tuning: core/memory offsets (driver-validated), power limit, voltage boost,
  clock-lock undervolting with wizard presets, knob-by-knob apply results
- Fans: interactive curve editor, dual-direction hysteresis, zero-RPM window,
  ramp limiting, temp-source selection (core / hot spot / memory junction),
  per-fan RPM, true 0% duty
- Monitoring: full sensor suite incl. memory junction on RTX 50, instantaneous
  board power, plain-language throttle analysis, throttle headroom, graphs,
  CSV logging, tray tooltip, temperature alerts
- FPS: ETW present tracing (bundled Intel PresentMon), avg/P1/P0.1 and 1%/0.1%
  lows with labeled methods, frametime graph, click-through overlay
- Stability: compute burn test with bit-exact error detection and TDR capture,
  guided stability stepper
- Automation: per-game auto profiles with revert, global hotkeys incl. panic
  reset, profiles as JSON, scriptable CLI, TDR watchdog, crash recovery,
  tray/startup modes
- Verified end-to-end on RTX 5090 (driver 610.88)
