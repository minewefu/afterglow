# Changelog

## 1.3.0-beta.1 — 2026-09-01

- **Intel Arc support, milestone 5: parity confirmed where the design already
  paid for it.** FPS capture works on Arc with zero changes — PresentMon is
  Intel's own tool and the ETW present pipeline is vendor-neutral (verified
  live on the B390: a presenting app captured at 240 fps with P1/1%-low
  metrics and present-mode detection). Per-game profiles, automation rules,
  session history, CSV logging, the overlay, crash forensics, and the MCP
  server all ride the same vendor-neutral seams. The README now tells the
  two-vendor truth: a dedicated "Intel Arc support (beta)" section lists what
  is verified working and what this device honestly lacks, the Honest
  limitations split per vendor, and the no-kernel-driver pledge names Intel's
  documented stacks alongside NVIDIA's.

- **Intel Arc support, milestone 4: the stability lab runs on Arc.** The D3D
  stress engines' adapter binding is vendor-aware: the PCI vendor id is now
  part of the binding alongside the bus (0x8086 for Arc contexts, resolved via
  the same LUID→PCI-bus D3DKMT path, which works unchanged for Intel), so the
  burn test, VRAM test, transition/excursion patterns, stepper, V/F probe, and
  profile certification all target the exact card being tuned on any vendor —
  and an unbound `stress`/`vram` run on an Intel-only machine now tests the
  Intel GPU by default instead of failing to find an NVIDIA adapter (NVIDIA
  machines keep the historical largest-NVIDIA fallback, byte-for-byte). The
  VRAM test is honest about unified memory: it asks the D3D device itself
  (`UnifiedMemoryArchitecture`) and on UMA plans against the GPU's shared
  system-memory budget with a much larger safety reserve (a quarter of the
  budget stays free — every byte tested is a byte taken from the OS), then
  says exactly what it tested: "tested the GPU's shared system-memory budget
  (UMA) — this device has no dedicated VRAM". Verified live on the OneXPlayer
  3: a 20 s burn ran bit-exact with 0 errors, and a 25 s VRAM run detected
  UMA, planned 9.5 GiB of the 13.4 GiB budget, and verified 55 full rounds at
  ~20.8 GiB/s with 0 errors, printing the UMA note. Discrete-VRAM planning and
  every NVIDIA output are unchanged.

- **Intel Arc support, milestone 3: the first verified write path — the
  frequency clamp — plus the honest TDP verdict.** `ArcGpuTuner` now maps
  Afterglow's "locked core clock" knob onto IGCL's GPU frequency-range clamp
  (`ctlFrequencySetRange`), the one GPU-domain control the OneXPlayer 3's
  driver reports as controllable. Unlike NVML's lock, IGCL has a real readback
  getter, so every clamp apply and release either reports "(verified)" from an
  actual driver round-trip or fails loudly — a write the getter cannot confirm
  is reported as a failure, never a silent success — and `get` on Intel reads
  the clamp back from the driver, with a live "released" answer overriding any
  tracked shadow. Verified live: clamping to 450 MHz returned "100..450 MHz
  (verified)" and the core was enforced at 100 MHz under load; releasing
  restored "100..2300 MHz (verified)" with clocks recovering immediately. Apply/Reset/panic/ForceUnlock, applied-state
  stamping with the Intel identity, and crash recovery all flow through the
  same path. A power-limit write path (`ctlOverclockPowerLimitSetV2` with
  readback, unit-aware per the driver's capability block) is implemented and
  lights up only where the driver reports the knob supported — false on this
  iGPU, expected true on discrete Arc; field verification wanted. The honest
  TDP finding is recorded in docs/research/intel-driver-apis.md and in the
  app: no documented userland path answers for package-power writes on this
  device (no IGCL power domains, OC power limit unsupported, Sysman
  `canControl=false`, and ring-0 MSR writes are banned by project rules), so
  the Tuning page says exactly that — the clamp is the driver-supported lever,
  and the package budget is shared with the CPU either way. The Tuning page is
  vendor-aware without touching the NVIDIA rendering: on Intel only the knobs
  Afterglow actually drives appear (no degenerate 0..0 sliders), the clock-lock
  card describes the clamp rather than the RTX 50 undervolt method, the slider
  floor comes from the driver's own domain minimum (100 MHz here), and the
  `caps` header now says "knobs Afterglow can drive on this device".

- **Intel Arc support, milestone 2: live monitoring in the app.** The hardware
  layer is vendor-plural: `GpuManager` now initializes IGCL alongside NVML/NVAPI
  and produces a `GpuContext` per Intel GPU (numbered after the NVML devices, so
  per-index history, fans, flight recorders, and the GPU selector work
  unchanged), each with a reboot-stable `INTEL-<domain:bus:device.function>-<deviceid>`
  identity for profile/state stamping — the IGCL LUID changes every boot and is
  never persisted, and the full PCI location fits inside the 12-character
  prefix per-GPU state files key on, so identical cards can never share a file. A new `IntelSensorSource` feeds the existing telemetry pipeline
  from IGCL: core clock, board power and GPU utilization derived from the
  driver's monotonic energy/activity counters (unit-checked; counter resets and
  missing samples yield an honest "—", never a guess), session energy, media
  clock, shared-memory use — the dashboard VRAM tile now says "shared" on
  UMA iGPUs, where the figure is the GPU's allocatable budget rather than
  dedicated VRAM. The tuning surface is behind a new `IGpuTuner` interface
  extracted from `GpuTuner` with its exact NVIDIA signatures (that path is
  deliberately untouched — it cannot be regression-tested on this machine);
  the Intel implementation reports every capability false in this beta, and the
  page gates learned a capability-aware branch that applies to non-NVIDIA GPUs
  only (the NVIDIA gates are untouched, like the rest of that path): Tuning
  says "monitoring only in this beta", Fans says the fans are
  firmware-controlled, the V/F and stepper pages name the missing knobs, and
  the `caps` header says the flags are Afterglow's not-implemented-yet policy
  rather than calling them driver-reported. `ReadCurrent`'s power-limit slot
  became nullable so `get` and the MCP status report "not supported"/null on
  Intel instead of a fabricated 0 W (NVIDIA still always reads a real value
  back). Verified live on the OneXPlayer 3: the app starts on the Arc B390
  with a populated dashboard (550 MHz idle clock, watts, load, 1.0/13 GB
  shared budget) and honest "—" for the temperature, fan, and voltage sensors
  this device does not expose. `afterglow-cli monitor` works on Intel-only
  machines (in `--json`/`--once` it primes Intel's counter-based metrics with
  a second sample; NVIDIA output — down to its unenriched CLI field set and
  single immediate poll — is byte-identical to before), and "No NVIDIA GPU"
  errors became "No supported GPU"/"GPU(s) detected" across the CLI's
  vendor-neutral enumeration paths.

- **Intel Arc support, milestone 1: interop layer + multi-vendor selftest.** New
  IGCL (Intel Graphics Control Library, `ControlLib.dll`) and Level Zero Sysman
  (`ze_loader.dll`) bindings in `Afterglow.Core/Interop`, grounded field-for-field
  in Intel's official headers — every struct layout is pinned by unit tests against
  sizes and interior field offsets compiled from `igcl_api.h`/`zes_api.h` themselves
  (clang record-layout dump, procedure in docs/research/intel-driver-apis.md); on the
  IGCL side the Size/Version protocol additionally has the driver check each struct's
  total size at call time (Sysman's stype/pNext structs get no such runtime check —
  the unit tests are their only net). `afterglow-cli selftest`
  now probes three stacks independently — NVML no longer exits the self-test on a
  machine without NVIDIA hardware — and prints every Intel capability truthfully:
  bulk power telemetry (energy counters → watts, activity counters → utilization,
  throttle flags), frequency domains with ranges and clamps, temperature sensors,
  memory modules (shared vs dedicated location — the honest UMA signal), engine
  groups, fans, power domains with PL1/PL2/PL4 limits, the per-knob overclock
  capability report, and the V/F curve entry points. Verified live on an Arc B390
  iGPU (OneXPlayer 3, driver 32.0.101.8991): telemetry, clocks, utilization, and
  frequency-clamp reads all answer; the same run records what this device honestly
  lacks — zero temperature sensors, zero fans (EC-controlled), zero IGCL power
  domains, `bSupported=false` on every overclock knob, and `ErrorDataRead` from the
  V/F curve reads. One trap this exposed is now load-bearing design: the OC getters
  "succeed" with zeros even where the capability report says unsupported, so all
  future gating keys off the capability report, never off getter status. The app
  itself does not consume the new interop yet — that is milestone 2 (telemetry) —
  and nothing writes to the hardware: selftest is read-only. Discrete Arc owners
  (B-series especially): please run `afterglow-cli selftest` and paste the output
  into a GitHub issue — the OC/power/temperature answers are expected to differ
  from this iGPU's.

## 1.2.0-beta.4 — 2026-08-30

### Fixed — independent review of 1.2.0-beta.3 (all 8 high-severity findings)

- Automation rules: the "apply a profile" action now lands on the card that
  actually breached, not on whichever card the profile was stamped for or the
  title bar happened to show. A profile saved for a different card is refused
  outright — before any clock or fan moves — and the log line and tray balloon
  now report what really happened, including a refusal or a partial apply,
  instead of always claiming success. The action also no longer hands the
  breaching card's fans back to firmware
- Applied state: a legacy single-GPU record stamped for another card is no
  longer handed to a second GPU, which could copy that card's tracked clock
  lock into the second card's file under the second card's identity — a silent
  ~half-boost cap on a knob NVML has no getter for. Unstamped records from a
  genuine single-GPU upgrade still migrate
- Fans: "Firmware (auto)" now always issues the driver release, so it can
  recover fans left in manual mode by the per-fan buttons, `afterglow-cli set
  --fan`, the MCP fan tool, or an unclean exit — previously it silently did
  nothing and still reported "restored". A refused release now says so instead
- Profile apply: a saved profile that recorded no per-point V/F offsets now
  removes point offsets still in force (reported as its own knob, like the clock
  lock), so certification can no longer stamp a profile stable against a curve it
  never tested. Only profiles that actually read the table when they were saved
  can remove a curve — a partial `set`, an MCP tuning call, a stepper step or the
  post-game restore says nothing about the curve and leaves it alone
- V/F probe: a probe is bound to the card it started on — switching the
  title-bar GPU mid-probe no longer redirects sampling to the other card while
  the probe keeps locking clocks on, and saving results into, the first. Each
  card's curve recorder now refuses samples from any other card
- Stability stepper: Stop now takes effect within half a second instead of
  after the rest of the burn step (up to 5 minutes of burning at an offset the
  user just abandoned), quitting mid-run waits for the starting offset to be
  restored (and says so in the log if it could not), and a stepper instance that
  is still unwinding now refuses to start a second run on the same GPU
- Start with Windows: the elevated, no-UAC logon task is refused unless the
  executable sits where only administrators can change it. Registering it from
  a portable copy in a user-writable folder handed anything able to rewrite that
  exe administrator rights at every logon. The default install location passes;
  an install redirected to a user-writable folder does not, and the refusal
  explains what to do. A task registered before this check is not removed — the
  Settings toggle now warns when the running exe's location would be refused
- The multi-GPU selector in the title bar is clickable again — it sat inside the
  window-chrome caption band, so every click dragged the window instead of
  opening the list

### Changed

- The per-point curve documentation now matches measurement rather than
  inference: a core-offset write lands on every point of the shared clock-boost
  table at once (so applying one erases per-point edits), and clearing the point
  offsets left the global core offset intact on RTX 5090 / driver 616.56 — but
  rather than depend on that, any clear Afterglow performs during an apply is
  followed by writing the core offset again

- **Per-point V/F curve editor** (#1) — the Afterburner-style mechanism, on the
  V/F Curve page and as `afterglow-cli vfpoints`: the driver's stored curve
  table drawn over the measured curve (gold dashed), per-point offsets, a
  one-click flatten undervolt (raise the point at the target voltage, cap
  every higher point), clear-all, and profile capture/apply of point offsets —
  every write verified by reading the table back. Interop rebuilt on the
  field-proven nvapioc layouts after discovering the original struct never
  worked anywhere: **the long-standing "RTX 50 blocks the curve interfaces"
  claim was our own broken layout, not the driver** — read and write are now
  verified live on RTX 5090 (127 points, delta scale calibrated against a
  known global offset), and RTX 20/30/40 are expected to work via the same
  interfaces (awaiting field confirmation). The capability is probed live,
  never assumed by generation.

- Multi-GPU support, phase 1 (#1): a title-bar GPU selector (visible when
  more than one NVIDIA card is present) points every page — dashboard,
  tuning, fans, V/F curve, stability, profiles, FPS session recording, the
  overlay, and the tray tooltip — at one card. Stress, VRAM, certification,
  the stepper, and the V/F probe bind their D3D adapter to the tuned card's
  PCI bus (resolved from the adapter LUID via D3DKMT — never by enumeration
  order; on a requested bus with several NVIDIA adapters and no match, the
  test refuses instead of guessing). Profiles are stamped with the GPU they
  were saved on and refuse to apply to a different card; game rules and
  startup apply target the stamped card. Applied-state/crash-recovery records
  are per-GPU files (two cards can never overwrite each other; the old single
  file stays readable until superseded), and each GPU records its own V/F
  curve (previously all cards fed one curve). CLI: `--gpu N` on
  stress/vram/certify/vfcurve and `mcp --gpu N`; new `stress --probe-adapter`
  diagnostic prints each GPU's NVML-bus → D3D-adapter mapping.
- Multi-GPU support, phase 2: fan configuration is saved per GPU (the Fans
  page edits and restores the selected card's own mode/curve at startup;
  pre-multi-GPU settings migrate to the primary card), automation rules
  watch every GPU independently (breach time and cooldown per card, the fan
  action pins the breaching card's fans, alerts name the card), and the
  flight recorder keeps one black box per GPU (primary keeps the original
  flight directory; secondaries get flight\gpuN — crash forensics scans all
  of them). Honest limit: dual-GPU behavior is still not verified on real
  dual-NVIDIA hardware; that verification is what the 1.2.0 betas are for.

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
