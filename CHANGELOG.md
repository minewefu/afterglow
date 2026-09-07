# Changelog

## Unreleased — post-beta.1 review fixes

A full review of 1.3.0-beta.1 (and of 1.2.0-beta.4 underneath it) found defects
across both, including several that let the tool report a stability verdict it
had not earned. Everything below is fixed; the Intel findings were reproduced
live on the Arc B390 the beta was verified on.

- **A burn that never stopped is no longer scored as a pass.** `StopAndWait` now
  reports whether the worker actually finished, and every consumer that produces
  a verdict treats "still running" as *no verdict* instead of success: the
  stability stepper no longer advances to a higher offset on it,
  `ProfileCertifier` refuses to stamp a certification from it, `stress`/`vram`
  exit non-zero and say the figures are a stale mid-run snapshot, and the MCP
  tools return `stable: false` with `inconclusive: true`. (The V/F probe holds its load
  engine to the same 30 s budget and reports a join that timed out as an
  unknown closing check rather than a clean one.) Certification
  additionally requires that the burn ran real *load* dispatches — the engine
  counts a one-off reference pass too, so the obvious "did any work happen?"
  test was a tautology that a zero-iteration run still satisfied — and re-checks
  that the
  profile is *still applied* before stamping, since an automation rule or TDR
  reset mid-run used to leave the remaining modes passing against stock clocks.
  That check uses the knobs that can actually witness a reset: the core and
  memory offsets, plus the clock lock on Intel, where the driver reads it back
  (NVML has no locked-clock getter, so there it would only be an in-process
  shadow). The power limit is excluded on purpose — a reset restores the board
  default and profiles routinely carry that same default, so it cannot tell
  "reset" from "still applied". A burn also now runs a **final** bit-exact verification
  before reporting success, so the last seconds of a run — the moment a marginal
  clock actually slips — are no longer left unchecked.
- **The burn test now bounds how far submission can outrun the GPU.** Without
  back-pressure it queued thousands of dispatches in the first second and then
  blocked in verification until the backlog drained (24 s on the Arc B390):
  `--seconds 5` took 20 s, the reported dispatch rate was meaningless, and the
  final line printed a one-second-old snapshot. A small ring of event queries
  keeps enough work in flight to stay saturated while capping the backlog — the
  wait spins briefly and yields before it sleeps, because a bare
  `Thread.Sleep(1)` rounds up to the OS timer quantum and would have capped
  submission at a few hundred batches a second on any card fast enough to retire
  one sooner; `--seconds 5` now takes
  ~6 s, the rate readout tracks real throughput, and the summary is the run.
- **The burn's working set no longer collapses on unified memory.** It was sized
  from `DedicatedVideoMemory`, which on the Arc B390 is a 128 MiB token against a
  13.4 GiB real budget, so the memory side of the burn ran at its 64 MiB floor.
  UMA devices now plan against the same shared budget the VRAM test uses.
  The dedicated-VRAM planner is otherwise unchanged, except that the size is now
  rounded down to the buffer's 16-byte stride on every adapter — which also
  repairs a latent `E_INVALIDARG` on dedicated cards whose VRAM/6 lands
  unaligned (exactly 2 GiB among them).
- **The Intel power-limit write path can now actually succeed.** IGCL refuses
  overclock-block writes until the session signs the driver's overclocking
  waiver, and nothing signed it — so on the discrete hardware where the knob
  lights up, every power-limit apply *and* reset would have been refused. It is
  now signed lazily, immediately before the first such write, and never while
  probing. Both the apply and the reset are held to the project's own rule: a
  value the getter cannot confirm is reported as a failure, not a silent success.
- **Vendor driver libraries load from System32 only.** `ControlLib.dll` and
  `ze_loader.dll` used default probing — application directory first — inside a
  process that runs elevated, while NVML had been pinned for exactly that reason.
  All three now resolve by full path through one shared resolver, and that
  resolver **throws** when the library is not in its installed location rather
  than returning "unresolved": returning unresolved hands the lookup back to
  default probing, which would have left the same planting hole open on any
  machine lacking that vendor's driver. Verified by planting a file named
  `nvml.dll` beside the executable and confirming it is not loaded.
- **Sensors that do not exist are never reported as zero.** `get` and the MCP
  state/telemetry tools return null (not 0) for offsets the device has no knob
  for; `monitor` prints "—" instead of blank columns, a bare `P`, or a fabricated
  0 MiB, and labels shared memory as shared; the Stability page's live line, the
  MCP burn/VRAM peak sensors, and session power/temperature averages all stop
  folding missing readings in as 0 — session averages now keep a counter per
  metric, and an unmeasured metric exports as "—" rather than a measured 0.0 °C.
  The dashboard sparkline breaks its line at missing samples instead of drawing a
  convincing flat trace along the axis for a sensor the card does not have.
  `selftest` renders driver "unknown" sentinels as "—" rather than `gen-1 x-1`,
  `bus -1-bit`, or `type 2147483647`.
- **Clock-lock honesty.** Asking to lock above the GPU's maximum used to report
  `ok … (verified)` for a clamp that is the unrestricted range — `get` then said
  "none" a second later; it is now refused with the reason. A clamp left behind
  by a crashed session is no longer adopted as the "released" baseline, which had
  made a real leftover clamp permanently invisible. A release is judged from
  the readback: the floor must be back at the minimum, and the ceiling must not
  sit below one this process has already verified released — that, and only
  that, fails. A ceiling that rose, or reached the domain maximum or a verified
  ceiling, is reported "verified"; one that merely stayed where the clamp was is
  adopted as the GPU's factory ceiling and said so (the driver's factory restore
  is measured to return the factory range, see the research notes). No state is
  changed until the readback passes, so a failed release keeps the tracked clamp
  for the next apply to retry.
  `ReadCurrent` takes the apply lock, so a concurrent apply can no longer be
  overwritten with a pre-apply reading. And `--lock-clock off` performs one
  release, not two.

  *Factory ceilings below the domain maximum:* on a GPU whose factory ceiling
  sits below the frequency domain's maximum — never observed on the tested B390,
  which reports 100..2300 from both getters — Afterglow cannot tell that ceiling
  apart from a clamp left by a crashed session until it has tried a release, so
  `get` reports it as a clamp. The first lock-less apply, `--lock-clock off` or
  `reset` then issues the driver's factory restore; when the driver accepts it,
  the floor is back at minimum and the ceiling still does not move, that ceiling
  is adopted as the GPU's factory value — the release says so rather than
  "verified", and later reads stop reporting it as a lock. (An earlier cut of
  this fix failed the release instead, which made the phantom permanent: every
  lock-less apply came back PARTIAL and persisted the ceiling as a lock that no
  reset could clear.) Only a ceiling that was merely *observed* is adopted this
  way, and so is a clamp Afterglow wrote at that ceiling — a request above it
  settles at it, which is measured, so refusing to release such a lock made a
  second phantom. A
  lock-less apply releases an inherited clamp again, from every front-end,
  as it did in beta.1, and it reads the live range first when nothing is
  tracked, so a fresh CLI `certify` sees the same state the App's Tuning page
  would have.
- **The V/F probe can no longer leave the GPU pinned or overstate what it did.**
  It pins an exact clock and undoes that only in its worker's `finally`, on a
  background thread — so exiting mid-probe left the pin in place until reboot.
  App shutdown now cancels and waits for it, and `vfcurve --probe` installs the
  Ctrl+C handler every other long-running command already had, so an interrupted
  sweep unwinds instead of terminating with the clock still pinned. The probe
  also watches its own load engine now: a load that cannot run would otherwise
  have produced a sweep of idle voltages reported as a measured curve. An aborted sweep is reported as aborted
  with the step it reached, instead of "Probe complete — the curve below is your
  GPU's measured V/F map", and a failed restore is surfaced loudly. A refused
  clock lock reports the driver's actual reason rather than always blaming
  administrator rights. An undervolt plan is no longer produced for a GPU with no
  core-offset knob, and `vfpoints --clear` verifies the clear and re-applies the
  global core offset the zeroed table can take with it.
  A probe that cannot release its pin now records that itself, per card, in the
  applied-state store — the store keeps that record through the clean-shutdown
  mark and the next-launch banner names the probe. On Arc the
  sweep stops at the ceiling the driver reports, an exact pin's release is proven
  by the floor dropping back to minimum (the ceiling of a factory-limited card
  never "rises"), and every "keep the current lock" path — CLI `set`, MCP, the
  stepper, game rules, the Tuning page — carries forward the lock Afterglow
  applied, never a ceiling it merely observed. The tuner itself retires a
  probe's pin record on every verified lock release or re-apply (`set
  --lock-clock off`, `reset`, a lock-less apply), so the App's "may still be
  pinned" banner tracks the hardware rather than the last front-end that
  touched it; the probe restores the clock BEFORE the load's 30 s teardown,
  and releases any clamp it finds before sweeping so the targets are capped at
  the card's true ceiling rather than at a leftover pin.
- **The clock-lock state machine is now under test.** `IArcDevice` and
  `IProbeLoad` seams let the real Arc tuner and V/F probe run against an
  in-memory frequency domain (`FakeArcDevice`) and a load that runs nothing,
  and 39 scenario tests encode every clock-lock finding from the review passes
  — a change to the tuner has to keep all of them true at once. The harness
  caught, and the batch fixes: a leftover pin whose cap the driver kept being
  adopted as the factory ceiling; a probe's own pin counting as "the lock
  Afterglow applied" (so a failed release was restored as a range lock on the
  next sweep and on every stepper step); a lock written at the factory
  ceiling refusing the sweep; the probe flag rewriting a clean record's
  shutdown state; a fan record deleted with the probe flag it shared a file
  with; the exit-time re-record racing the worker's own resolve; a load engine
  that failed to stop reported as "the GPU proved unstable"; and Reset hiding
  the banner over a record it could not resolve.
- **The Arc tuner's clamp state is one value.** Seven loose fields (the tracked
  clamp plus three provenance booleans, the released baseline plus its
  "observed" flag, and the ceiling high-water mark) became a `TrackedClamp`
  record with a provenance enum (observed / inherited / written) and a shape
  enum (range / exact pin), plus a nullable observed ceiling. Illegal
  combinations — a clamp both written and inherited, a pin flag with no clamp —
  can no longer be represented, so the release rules read as a decision on one
  value rather than on four fields that had to be reset together by hand. The
  frequency-range semantics were measured on the B390 the same day and are
  recorded in `docs/research/intel-driver-apis.md`: a factory restore always
  returns the full range, writes above the domain maximum are clamped rather
  than refused, and fractional requests are truncated on readback.
- **One record per card, written by the tuner.** Every applied-state writer now
  files under the card's stable key (`IGpuTuner.RecordKey`: the UUID, or
  `index:N` when the driver reports none) — the tuner, the fan service, and the
  V/F probe's pin, which the tuner itself puts on record before the pin lands
  and resolves on every verified release or re-apply. A process killed
  anywhere in a sweep, or a shutdown whose join timed out, leaves the truthful
  record behind without any front-end composing it, so the probe's own record
  writes, its in-flight flag, and the App's exit-time re-record are gone. The
  pre-multi-GPU single file is adopted into the card's own file the first time
  its tuner starts (an Intel identity never adopts an unstamped one) and is
  otherwise only listed, so an orphan still raises the banner and Dismiss
  clears it; the legacy-file ownership heuristics that every write and clear
  used to run are deleted.
- **An explicit unlock is one operation with one verdict.** `Apply` takes a
  `releaseLock` option: the tuner releases the clock lock itself — whether or
  not this session tracks one — and reports it as the "clock lock" knob. The
  CLI's `set --lock-clock off` and MCP `apply_tuning` with `unlock: true` each
  used to release first and then reconcile that against Apply's own release by
  knob name, printing two lines for one request or a FAIL beside a verified
  release; both are now a single call.
- **Review of the harness batch (medium effort, 8 findings, all applied).** The
  NVIDIA restore path never resolved the pin record, so every successful probe
  under a user lock raised the "may still be pinned" banner; a refused pin
  write erased an older session's still-true pin record; the upgrade path
  dropped a legacy record's lock when the card already had a probe record; an
  explicit unlock on an Arc without the clamp reported nothing and exited 0; MCP
  accepted `unlock` beside `lock_clock_mhz` and silently dropped the lock; and
  the pin flag lost the lock the pin displaced. The Arc release verdict is now
  one rule — the readback ceiling must not sit below one this process has
  verified released — which releases a lock written at the factory ceiling (a
  measured shape the old branches refused) and drops the cap-keeping hypothesis
  those branches defended against.
- **The tuner owns a sweep's start and end.** `IGpuTuner.BeginProbe` remembers
  the lock this process applied, releases whatever clamp is on the card so the
  sweep runs against the true ceiling, and says how high it may pin;
  `EndProbe` puts the lock back or releases the pin. Only the tuner can capture
  the displaced lock before its own release empties the tracking — the probe
  capturing it from outside lost it on Arc, so a game rule or an MCP apply
  during a sweep dropped the user's lock from the record. The probe's own
  restore-versus-release choice, its pre-release, and its "did a pin land"
  bookkeeping are gone with it. An apply that carries a lock and asks for the
  release is refused by the tuner before anything is written, so no front-end
  resolves that contradiction silently.
- **The stepper refuses GPUs with no core-offset knob** instead of burning a full
  cycle at offset 0 and reporting "+0 MHz" as a confirmed stable offset — which
  `find_stable_offset` handed straight to an agent.
- **Fans can no longer be left pinned silently.** The per-cooler buttons write
  driver-persistent manual control, but did so without arming the fan service, so
  nothing recorded applied state and — if the service had never been engaged that
  session — nothing released them on exit. They still issue the per-cooler write
  directly (the service commands all coolers at once), but now register it with
  the service, so the release path, the applied-state record and the
  unclean-shutdown banner all cover them. A release the driver refuses at
  shutdown re-marks the session unclean so the next launch warns, instead of
  being logged and forgotten.
- **Crash forensics.** The event-log search ran only to +20 minutes, but the two
  hard-reset signatures it depends on (Kernel-Power 41, EventLog 6008) are
  written at the *next* boot — any reset where the machine stayed off longer
  reported "no crash". Those two now search to the present, while the
  fault-stamped signatures (TDR 4101, WHEA, GPU kernel-driver faults) keep the
  tight window so a later, unrelated TDR cannot manufacture a verdict; and the
  *first* bugcheck code wins, so a later bluescreen cannot displace the one being
  analysed. Intel and AMD kernel display drivers now count as driver events (only
  NVIDIA's did). On a multi-GPU machine every card's flight log is analysed, the
  card that was actually tuned is preferred, and the report body names the GPU —
  instead of always reporting the primary's offsets, which exonerated a tuned
  secondary card.
- **FPS numbers go stale honestly.** Frame stats are stamped with a wall clock and
  stop being served after ~2 s without a frame, so the overlay and dashboard tile
  no longer repaint a frozen average indefinitely after capture stops.
- **Smaller fixes.** `monitor --csv session.csv` no longer crashes on a bare
  filename; `monitor` accepts `--gpu` and does not throw when redirected to a
  file; `afterglow-cli version` exists; a VRAM run stopped mid-round no longer
  discards errors the GPU had already counted; IGCL adapters that are not Intel
  are skipped rather than becoming phantom "Intel" GPUs; an adapter whose PCI
  location the driver never reported no longer gets a fabricated `00:00.0`
  identity or stress binding; the Intel counter state is guarded against
  concurrent polls; the dashboard calls Intel's lifetime energy counter "since
  driver load" rather than "session energy"; comparison exports carry the
  different-application warning; and clipboard copies no longer raise a modal
  crash dialog when another process holds the clipboard.

### Fourth review round — eight blocking defects, and the twins of the third round's fixes

The third round's fixes were themselves reviewed (ten lenses, three refuting
skeptics per candidate, then a judge). It failed the branch. The pattern was the
one this codebase keeps repeating: the previous pass had repaired two of the four
places the stepper restores an offset and left the two that follow a *detected
hardware fault* — the ones where the driver is most likely to refuse the write.
All eight blockers are fixed, along with every medium the round raised and eight
more this pass found by auditing its own changes.

- **The stepper could report a completed sweep with a stable offset while the card
  sat at the offset that had just crashed it.** Two of its four restore paths
  discarded `ApplyOffset`'s result, logged nothing, and still published
  `phase: "done"` — so MCP `find_stable_offset` returned
  `all_succeeded: true, stable_core_offset_mhz: 0` over a GPU still at the offset
  that had produced computation errors or a driver reset. A third path (the
  backoff write failing) attempted no restore at all and left its own
  "Backing off to +75 MHz…" line standing as the last word in a log both the app
  and MCP agents read. Every terminal path now goes through one helper that
  restores, says so when it cannot, publishes where the card actually is rather
  than where it was asked to be, and refuses to call a run "done" when the restore
  failed. `Finish` likewise no longer claims an offset is applied without checking.
- **MCP `find_stable_offset` surfaced the restore only on its timeout branch.**
  The hardware-failure paths return through the normal branch, which reported just
  phase and `all_succeeded`. It now carries `start_offset_restored` and the offset
  actually applied on every exit, and folds a failed restore into `all_succeeded`.
- **MCP `reset_defaults` and `apply_tuning{fan}` could report success for work that
  did not happen.** `reset_defaults` omitted `all_succeeded`, so a wholly refused
  reset arrived as a successful call; `apply_tuning` with a fan value that was
  neither `"auto"` nor a number issued no command, added no result, and left
  `all_succeeded` vacuously true. The fan argument is also read *before* any write
  now — it was the only one parsed afterwards, so a JSON number threw after the
  clock and power knobs had already landed.
- **Certification asserted a reset it never checked, and sometimes never performed.**
  `ProfileCertifier` discarded `ResetToDefaults`'s result and the UI stated "the GPU
  was reset to driver defaults" as fact — including on the apply-failure path, which
  attempts no reset at all and leaves a partly applied profile on the card. The
  status now carries what actually happened and the UI reports it. Relatedly, a
  PARTIAL reset no longer erases the applied-state record: that record exists
  precisely for the case where the driver refused to undo something.
- **The fan service could re-command the fans after releasing them.** `Dispose`
  cleared the "we control the fans" flag but left the curve mode and evaluator
  live, so one telemetry snapshot arriving afterwards passed every guard — the
  cleared duty even defeated hysteresis — and wrote a duty *after* the release,
  leaving the card on manual fans (0% on a zero-RPM curve) once the process exited,
  with the session already stamped clean. The curve is now stopped in the same
  lock, and shutdown stops telemetry before releasing the fans rather than after.
- **A trailing `--gpu` was invisible.** The scan stopped one token short of the end,
  so `set --core-offset 200 --gpu` read as "no card requested" and wrote to GPU 0 —
  the same silent retarget the tri-state parse was added to prevent, reached by a
  different route. Fixed in the shared parser, so every command is covered at once.
- **`drs` verified its own writes against the predicate that drove them.** A
  `--vsync` value outside `default|on|off` meant "not enabled", which *deletes* the
  stored setting — and the verification derived its expected value from the same
  test, so it compared against `"default"`, exactly what the delete produced, and
  could never fail. `drs --exe game.exe --vsync yes` therefore removed the user's
  vsync override and reported "(verified)". Values outside the legal set are now
  refused before any driver write, `--low-latency` likewise (anything but `on`
  silently meant *off*), and the readback compares against what was requested.
- **`MarkCleanShutdown` ran on every early-exit path.** WPF runs `OnExit` for the
  single-instance short-circuit, `--register-startup` and the self-elevation
  relaunch, so a second process could rewrite the resident instance's record to
  clean and suppress the next launch's warning about a still-tuned card.

Also fixed in the same round:

- A verified Intel release was reported as a FAILURE at the domain maximum — and
  the previous round's fix for that opened the opposite hole, reporting a clamp at
  `hwMax-1` that the driver had NOT released as "released … (verified)". The
  tolerance is now tight enough that a real release is distinguishable, and the
  domain-maximum shortcut fires only where no rise is possible. Verified live.
- The Intel tuner asserted "a clamp is present" from its persisted shadow without
  reading the driver, on the one tuner whose getter works — reachable from MCP
  `apply_profile`, which does no prior read. It now asks the driver first.
- A panic reset could freeze the window for up to ten seconds: it is invoked from
  the message pump, and the stepper's restore alone retries for ~4.5 s. The stop
  requests stay inline; the waiting and the reset now happen off the pump. It also
  waits for the burn and VRAM workers, not just the stepper — otherwise a plain
  burn kept running through the reset and still published "stable under this load"
  for a run whose second half executed at stock clocks.
- A Ctrl+C-aborted `stress` or `vram` exited 0 — which `docs/agent-integration.md`
  tells automation means "stable". Both now report the abort and exit 1.
- `vfpoints --clear`'s readback was stricter than the rule it cited: a getter that
  did not answer reported a working clear as failed. It now matches `ApplyOffset` —
  a mismatch fails, an unanswered getter means "not verified". The reconcile path
  also clamps the offset once and reuses it, instead of verifying against a value
  the driver was never going to store.
- The persisted V/F curve had no identity guard on the WRITE half, so a foreign
  card could overwrite the file the read half correctly refuses — worst with
  `--fresh`, which skips the read entirely.
- The V/F probe's "restore failed" latch was never cleared once consumed and was
  reset by any later probe, so a second probe on another card erased the record
  that the first had left a card pinned, and shutdown then stamped the session clean.
- Stale per-card state survived a GPU switch on the Stability, V/F and Fans pages —
  the Tuning page had been fixed, its three twins had not.
- MCP `save_profile` wrote no GPU stamp, short-circuiting the cross-card apply
  guard for every profile an agent saves. An `UnauthorizedAccessException` from a
  profile write escaped the tool-call catch filter and killed the stdio server.
- The metrics session report was stamped with whatever GPU was selected at Stop and
  carried no identity; it is now bound to the card the capture started on.
- `TelemetryService.HistoryFor` threw for an index with no poller — from a render
  tick, that takes the window down. The Intel self-test computed watts from an
  unsigned energy delta that underflows to an astronomical figure if the counter
  wraps. The dashboard's FPS row kept the last app's value. `monitor` reported a
  bad `--interval` value as an unknown option.
- Documentation corrected where it described behaviour the code does not have: the
  Intel waiver is signed automatically by the tuner (there is no in-app warning
  gate), the per-point V/F editor ships rather than being roadmap, and RTX 50
  rejects *whole-table* writes while accepting the per-point offsets the shipped
  command uses. `docs/agent-integration.md` now documents exit code 2 — arguments
  rejected, nothing ran — so an agent cannot read a rejected argument as instability.

- The new identity guard on the curve WRITE refused silently, so `vfcurve --fresh` on
  a swapped card would have measured a full curve and then quietly not kept it. The
  CLI now says so (and `--json` carries `saved`), as does the app's Reset curve.
- Auditing this round's own stepper fix found two of its twins: the app's status
  line rendered a failed or cancelled sweep as the bare word "failed", saying nothing
  about whether the card had been returned to its starting offset (the MCP payload
  had just been fixed to say exactly that), and the end-of-sweep warning claimed the
  GPU "is not at the offset reported" when the last burn had in fact run at that
  offset and only the confirming re-write was refused. Both now say what the
  evidence supports and nothing more.

The CLI's option surface now lives in one table, and `CliContractTests` drives that
table rather than a list of its own: a command that reads an option without
declaring it fails a test. That check was written because the previous round's
argument hardening silently broke three of `drs`'s hidden diagnostic flags, and it
was verified by reintroducing that exact bug and watching the suite go red.

### Third review round — twelve blocking defects

An independent adversarial review (ten review lenses, every candidate put to three
skeptics prompted to refute it, then a judge) failed the branch and found twelve
blocking defects. Two were regressions from the pass above; the other ten had been
in the code longer. All are fixed:

- **Panic reset could not stop a running stability sweep, and a reset landing
  mid-burn manufactured a pass.** The hotkey (and the automation `reset` action)
  called `ResetToDefaults` on every GPU without cancelling the stepper or the V/F
  probe. A reset twenty seconds into a sixty-second step ran the remaining forty at
  **stock clocks** and still scored the step as passed — an offset marked stable on
  evidence gathered while it was not applied, then published as the stable offset and
  handed to MCP agents; the next iteration re-wrote the offset the reset had just
  removed. Both are now stopped and waited for before anything is reset, and the
  balloon says so when something was still stopping.
- **MCP `reset_defaults` returned a wholly failed reset as a successful call.** It
  emitted `results` but not `all_succeeded`, and the error probe looks for exactly
  that flag — so after a driver refused every knob, an agent recorded a clean abort
  and left the GPU at the settings that had just crashed it. This was the one write
  tool without the flag, and it is the one the tool description calls "the safe abort".
- **MCP `find_stable_offset` claimed the offset was restored without checking.** On a
  timeout it waited for a terminal status and reported "the starting offset restored"
  — but a terminal status only means the worker unwound. The restore write gives up
  after three tries in about 4.5 s, well inside that wait. The stepper now records
  whether the restore actually landed, and the tool reports what it recorded.
- **`vfpoints --clear` reported the core-offset restore from the return code alone.**
  The clear can take the global offset down with the per-point deltas, so the offset
  is re-written afterwards — and that write was never read back, the exact
  accept-but-ignore case `ApplyOffset` guards against a few hundred lines below. It
  exited 0 while `get` reported 0 MHz. Both this and its apply-path twin now confirm
  by readback. (NVIDIA-only; found by reading, not running.)
- **An unparseable `--gpu` was silently discarded, so a named card became a guess.**
  `--gpu 1x` parsed to "no GPU requested", which is not the same thing at all: on a
  multi-GPU box `stress`/`vram` then set the flag that disables the engine's own
  "cannot tell which card this would burn — refusing" guard and burned the
  largest-VRAM adapter, exiting 0 — documented as "stable" — for a card the run never
  touched; `certify` applied the profile to GPU 0 and stamped it; `mcp` bound every
  tuning write and stability verdict to GPU 0; `set` wrote to it. `--gpu 9` was
  refused while `--gpu 9x` was not. Parsing is now tri-state and lives in the shared
  checker, so an unusable index is an error (exit 2) in every command at once.
- **Dashboard FAN and VRAM tiles kept the previous value when the sensor went
  quiet.** They were the only two conditional assignments among thirteen; their
  eleven neighbours fall to "—". One lost tick after a TDR froze the FANS tile on its
  pre-crash reading beside a live "%", and switching from a dGPU with fans to an iGPU
  without left the dGPU's duty and RPM rendered as the iGPU's. The fan capability
  also latched "unsupported" on *any* failure, permanently, rather than only on a
  return code that means unsupported.
- **Clearing the metrics display discarded the completed capture.** A regression from
  the pass above: the new blank-the-numbers helper also nulled the record
  `RecordSession` needs, so quitting the game and *then* pressing Stop — the most
  ordinary order — silently threw away the session with no message and no history row.
- **A persisted V/F curve carried no GPU identity.** A card swap, or any session
  where NVML failed to initialise and an iGPU became GPU 0, made the new card inherit
  the old one's curve: the load-time bounds check only rejects impossible numbers, and
  another card's 700-1100 mV at 2000-3000 MHz passes cleanly. It then rendered as this
  card's measurement and, on NVIDIA, could be turned into a real offset-and-lock write
  — in the direction that raises clocks. The file is now stamped with the GPU's UUID
  and vendor and discarded on mismatch, following the rule the applied-state store
  already used. (`Add()` had always refused foreign *live* samples; this closes the
  same hole on the persisted path.)
- **Two Intel clamp writes reported success without a readback.** `RestoreTuningLock`
  and `LockClockForProbe` returned the driver's code alone, contradicting this class's
  own promise that clamp writes "verify or fail loudly" — and their callers treat
  success as proof: the probe prints "previous clock state restored", shutdown stamps
  the session clean, the CLI emits `probe_clock_restored: true`. All off a call the
  driver may have accepted and ignored, leaving the card pinned. Both now confirm by
  readback.
- **A verified Intel release was reported as a failure at the domain maximum.** With a
  clamp tracked at (or one MHz below) the frequency domain's own maximum, the
  "did the ceiling rise?" test could never pass, so a complete release returned
  "release accepted but readback still shows 100..2300 MHz" — quoting the fully
  released range as evidence of a surviving clamp, exiting non-zero on a release that
  worked, and latching a false unclean-shutdown banner for the next launch.
- **A malformed JSON-RPC line killed the MCP server.** Indexing an array or a value by
  name throws `InvalidOperationException`, not `JsonException`, and nothing caught it
  — so a spec-legal JSON-RPC *batch*, a non-string `method`, or a non-object `params`
  terminated the process, taking the `reset_defaults` abort channel with it while
  tuning stayed applied. Worse, the server echoes back the client's requested
  protocol version, so it told a conformant client it spoke a revision whose first
  batch message would kill it. Every field is now read defensively and any surprise
  becomes one error response.
- **The Stability and Tuning pages kept the previous card's verdict after the
  selector moved.** The live telemetry line switches to the new card immediately and
  the view renders it in the same panel, so "Stopped after 00:10:00 with 0 errors —
  stable under this load" sat under a card that had never been burned. The V/F page
  already cleared its own for exactly this reason.

Also fixed in the same round:

- `vfcurve --load` read the burn's verdict one statement too early — before the
  dispose that runs its closing bit-exact check — so a fault raised by that final
  verification printed no warning and exited 0.
- MCP `run_stress` silently substituted the sustained burn for an unrecognised
  `pattern` and still returned `stable: true`. The CLI had just been fixed for exactly
  this; its unattended twin had not. It now refuses and runs nothing.
- A failed power-limit read was published as a real `0 W` through `get`, `--json` and
  the MCP state tool, because the return code was discarded. Absent is now absent.
- "Reset curve" could flip the new "this driver reports no core voltage" verdict true
  on a card that had just produced a curve: `Clear()` reset the positive counter but
  not the negative one. The verdict is now symmetric in both counters and both reset.
- The voltage-sensor check no longer concludes from idle reads alone. Silent at idle
  is not the same as absent, and refusing a card that would have reported voltage
  under load is the more expensive error, so it now asks again under the load the
  command was about to apply anyway.
- Certification staleness was judged against one process-wide driver version that
  preferred NVML's, so on a hybrid machine an Arc certification survived the Intel
  driver update that invalidated it and was falsely invalidated by an NVIDIA one.
  Certifications now carry the GPU they were earned on and are judged against that
  card's driver.
- `monitor --csv <unwritable path>` crashed with an unhandled exception and a raw
  .NET stack trace instead of reporting the path it could not open.
- `drs` and `vfpoints` swallowed unknown options, so `drs --exe game.exe --fps-cap 60`
  (the option is `--cap`) performed no write and exited 0.
- SECURITY.md claimed "no network requests: no telemetry, no update checks", which
  the README's own honest wording and the opt-in update checker both contradict. It
  now describes exactly what the one request is and when it happens.
- The README still advertised Intel "session energy", the label this pass had already
  corrected in the app to "since driver load".

### Fixes to the fixes

The pass above was itself reviewed before landing, which found eight blocking
defects in it — including one it introduced. All are fixed:

- **The new back-pressure could hang the burn worker forever.** The event-query
  poll had no timeout and no device check, and the query simply never signals
  after a device reset — so a TDR mid-burn spun the worker indefinitely, no
  terminal state was ever published, and the certifier waited on progress that
  had stopped advancing with the overclock still applied. It now reports
  `DeviceLost` on a reset (the very event the burn exists to catch) and has a
  bounded deadline behind that.
- `vfpoints --clear` read the core offset *after* the write that can destroy it,
  so in exactly the loss case it was written for it restored 0 and reported
  "0 MHz core offset re-applied". It now captures the offset first, and refuses
  rather than guessing when the getter does not answer beforehand.
- `set --lock-clock off` did not reliably reach the driver. An explicit unlock is
  now issued *before* the profile apply, in both the CLI and the MCP
  `apply_tuning` tool. Ordering it afterwards meant a rejected argument could
  skip the release entirely (apply returns early on a validation failure, before
  it reaches the clock lock), and on Arc it printed apply's refusal to touch a
  clamp this session did not write followed by the successful release — two
  lines and exit 1 for one operation that worked.

- **A mistyped option could give you a stability pass for a test you did not
  run.** `stress`, `vram`, `fps`, `certify` and `vfcurve` silently ignored
  anything they did not recognise, and silently fell back to the default when a
  value would not parse. So `stress --pattern transtions` ran the SUSTAINED burn
  — not the load/idle transition regime chosen precisely because it catches
  marginal memory offsets that any sustained burn passes — and reported a clean
  result; `certify --seconds 6O` stamped a profile off the 90-second default;
  `stress --json` printed a human report to a script expecting JSON. All five
  now reject an unknown option, an unparseable value, an out-of-range value and
  an unknown `--pattern` before any work starts, with exit code 2. (`set` and
  `monitor` already did this; the commands that produce stability verdicts did
  not.)
- **A GPU that reports no core voltage was told to keep waiting
 for a V/F curve
  it can never have.** Found live on the verified Arc B390, whose every telemetry
  read returns a null core voltage: a V/F curve is voltage plotted against clock,
  so nothing there is measurable. `vfcurve` sampled for the full run and printed
  "0 voltage points from 0 samples"; `vfcurve --probe` would have pinned the core
  clock through an entire sweep to record nothing; the app's V/F page sat on
  "Collecting… run a game or the burn test to draw the curve" indefinitely, and
  its Probe button would have locked the clock for the same empty result. All
  four now say plainly that core voltage cannot be read on this GPU — naming the
  cause: the driver's telemetry, or on NVIDIA a missing NVAPI pairing, which is
  Afterglow's limitation rather than the driver's — and that a
  curve cannot be measured — the CLI refusing in under a second, before any
  clock is touched. The conclusion is measured, not assumed: the recorder counts
  reads that arrived with a clock but no voltage, one miss is treated as the
  glitch it is, and a sensor that answers even once is never called absent.
  `vfcurve --json` now always carries `voltage_sensor` and `error`, and the refusal
  emits the same field set as a normal run, so a scripted consumer can tell "no
  curve because this GPU cannot have one" from "no curve yet" without
  string-matching, and never reads `undefined` for a field it gets every other run.
- **`vfcurve --load` never watched the load it was driving.**
 The passive
  recorder started the burn engine and ignored its verdict entirely, so on a
  machine where the load refused to start — a guessed adapter, no D3D device —
  every sample was an idle-voltage reading, printed under the heading "under
  load" with exit 0. That is a wrong curve, not a thin one. The engine's verdict
  is now latched across the intensity sweep and reported, and the command exits
  non-zero. The probe path already did this; the passive path did not.
- **The metrics page kept the last app's FPS on screen after it closed.** When
  nothing is presenting, only the header was blanked — average FPS, both
  percentiles, both lows, frametimes and the graph all stayed frozen at their
  last real values under "No presenting app detected yet". Every measured field
  is now cleared with the header. This was the third consumer of the same
  stale-null; the other two were fixed earlier in this pass.
- A V/F probe on a non-elevated session reported "this GPU may still be pinned"
  and latched an unclean-shutdown record, over a session in which nothing was
  ever pinned: the first lock was refused, and the unconditional release then
  failed for the same reason. A restore failure is now only raised if a pin
  actually landed.
- `vfcurve --probe` described a completed sweep whose load engine had reported a
  hardware fault as "Probe did not complete". Coverage, restore state and load
  faults are three separate signals and now get three separate messages.
- The Intel tuner did not record its own probe pin in `AppliedLockMHz`, so on Arc
  the shutdown guard that restores a pinned clock saw nothing to restore. It now
  tracks the pin exactly as the NVIDIA tuner does.
- After Arc declined to release a clamp this session did not write, the advice
  was to run the very command that had just been refused. It now says to clear it
  with an explicit unlock.
- The widened crash-forensics window and the certifier's drift check are both

  narrowed to what they can actually prove (see the crash-forensics and
  certification entries above). The widening initially left Kernel-Power 41 and
  EventLog 6008 unbounded, so an unrelated power cut long afterwards could
  manufacture a hard-reset verdict blaming settings from a much older session;
  those are now required to fall at or after the analysed session, and a session
  older than seven days is no longer correlated at all.
- The guessed-adapter refusal was wired into certification but not into the
  stepper or the V/F probe, both of which also attribute a result to one named
  card. All three set it now.
- The back-pressure poll's terminal report could be overwritten a moment later by
  the progress tick, so after a real TDR the Stability page's last line still
  read "verified clean so far". Progress ticks no longer publish over a terminal
  state, and an aborted cycle is no longer counted as a completed excursion.
- A failed clock-lock restore was surfaced by the CLI but not by the app, whose
  "previous clock state restored" message could print over a GPU that was still
  pinned. Both surfaces report it now.
- `vfcurve --probe` exited 0 and printed a previously stored curve when clock
  locking was unavailable — the one probe exit that never carried an outcome.
- Certification refuses to run when the adapter could only be guessed, rather
  than burning one card and stamping the verdict onto another. Unbound
  exploratory runs keep the historical largest-VRAM fallback unchanged.
- `find_stable_offset` reported the starting offset as a confirmed stable result
  even when the very first burn failed and nothing was ever verified at it.
- The Intel clock-lock slider no longer parks at exactly the device maximum, a
  value the engine correctly refuses as a no-op, which made Apply always fail.

A third review round found seven more, again mostly one-site-fixed-twin-missed:

- **The System32 pinning did not actually pin.** Returning "unresolved" from a
  DllImport resolver hands the lookup back to default probing, so on a machine
  without that vendor's driver a planted library beside the executable would
  still have been loaded into the elevated process — the exact hole the change
  claimed to close, and documented as closed. The resolver now throws.
- The unprovable clamp release adopted the readback as the released baseline
  anyway, two lines above the check that was supposed to prevent it — and an
  intervening rewrite had demoted beta.1's decisive test (a tracked clamp's
  ceiling must rise) to mere message wording, so an unlifted clamp reported
  "(verified)". The release path is rebuilt around beta.1's rule as an explicit
  first case, with no state written until a check passes, plus one addition:
  a readback at the frequency domain's own maximum is proof on its own, since
  nothing can be clamped at the maximum.
- The V/F probe set the guessed-adapter refusal and then never looked at the
  load engine, so a refused load produced an idle-voltage sweep reported as a
  measured V/F map. It now aborts when the load cannot run.
- Registering a per-cooler fan write clobbered the fan curve's hysteresis memo,
  which could strand the other coolers at their previous duty for a whole load.
- Crash forensics now correlates the boot-stamped signatures to the first boot
  after the session and to a 24-hour window — counting boot markers alone was not
  enough, because a session killed while Windows kept running makes the next real
  reset, days later, look like "the first boot after the session". The two
  signatures sit on opposite sides of their own boot marker (6008 is written
  before it, Kernel-Power 41 after — verified against a real System log), so they
  are gated separately; one shared test let the second boot's 6008 through.
  Beyond that, staleness now reaches the *verdict*: a reset logged more than half
  an hour after the session no longer produces "the machine reset instantly while
  the GPU was under sustained heavy load" naming the applied offset, but a hedged
  headline stating how long afterwards the record appeared and that it cannot be
  tied to this session. A session older than seven days is not analysed at all.
- The certifier's drift check was dead on Intel, where the clamp is the only
  knob. It now uses the clock lock as evidence where the driver actually reads
  it back (IGCL), and only there.

A ninth round passed the pass, and its remaining notes were closed too: the
clamp release checks a tracked clamp *before* the domain-maximum shortcut (whose
1 MHz tolerance left exactly one clampable value, hwMax-1, able to survive a
release and still report "verified"); an implicit release now fires only for a
clamp this session actually wrote, so on a GPU whose factory ceiling sits below
the domain maximum a lock-less profile apply no longer issues an unrequested
release and returns PARTIAL once a minute; app shutdown records the session
unclean when the V/F probe's clock restore was refused, instead of stamping it
clean over a still-pinned card; `vfcurve --probe` reports coverage and clock
state as the independent things they are (`probe_complete` and
`probe_clock_restored`), rather than printing "did not complete" over a 12-of-12
sweep; `selftest` gates the BDF line on the validity flag the rest of the code
already respects; and a multi-hour crash gap renders as hours rather than
"1200 min 0 s".

An eleventh round caught two more regressions from the tenth's fixes. The
"only release a clamp this session wrote" guard declined SILENTLY, so a
lock-less apply on a clamped Arc card returned full success with an empty
summary while the GPU stayed pinned — worse than the unconditional release it
replaced. It now says what is present and how to clear it, `Apply` can no longer
return success with no results at all (`All` on an empty list is true), an
explicit `--lock-clock off` releases before applying so one operation prints one
line, and certification refuses to stamp a burn run under a clamp the profile
never asked for. Separately, unhooking the load watchdog before teardown — which
fixed a false abort — discarded the burn's final bit-exact verification, taken
at the highest clock the sweep pinned and BEFORE the clock is restored. That
verdict is now read back after the join and carried as a hardware warning
alongside the coverage outcome, so the curve stays valid and the instability is
still reported.

A tenth round caught two regressions in the ninth round's own polish: the MCP
tools folded the engine's "could not run at all" failures (no adapter, no D3D
device, gave-up-waiting) in with real hardware verdicts, so a run that tested
nothing came back "not stable, not inconclusive" — and on the gave-up path
contradicted the engine's own detail text. The record now distinguishes a
hardware verdict from an absence of one, and both tools ask it. The Arc
"clamp this session wrote" flag was also set only on the fully verified path and
not by the probe's exact pin or the probe-restore lock, so those clamps were
never implicitly released; it is now set where the write happens.

An eighth round found the guessed-adapter refusal in the same shape as the two
guards above: opt-IN, set on the certifier, stepper and V/F probe, and missing
from both MCP engines and the Stability page — so an agent could still be handed
a verdict for a card the run never bound to. Refusing is now the engines'
**default**; only a deliberately unbound CLI `stress`/`vram` run opts out, which
is exactly where the historical largest-VRAM fallback is the documented
behaviour. Also: `vfcurve --probe --json` reported `0 of 0` steps on every
successful sweep (the counts were only assigned on the failure path); the crash
banner asserted "Last session ended in a crash." above the deliberately hedged
staleness verdict that says the opposite; and `VerifyDue` lacked the health guard
its `Tick` twin got, so after a detected TDR it would touch the device again —
throwing away the real diagnosis, or blocking on a hung device and reintroducing
the worker hang the fence deadline exists to escape.

A seventh round caught the mirror image of that guard. The burn count reached
only some of the engine's terminal reports, because the parameter carrying it
had a default — so a genuine driver reset published "0 load dispatches" after
thousands, and the consumers then *replaced* the real "the GPU was removed/reset
— the current clocks are unstable" message with "nothing was tested, retry with
more seconds", advising an agent to re-run the offset that had just reset the
GPU. The parameter is now required, so the compiler names every site that
forgets it (it found ten), and every consumer short-circuits on a detected
failure before any "did it do work?" reasoning: a detected artifact or TDR is a
*result*, never "inconclusive". The VRAM tool had a sharper version of the same
bug — its round counter only increments after a fully clean sweep, so it is
anti-correlated with detection, and found memory corruption was being reported
as "nothing was actually verified".

A sixth round found that the work-count guard above had reached three of the
five burn-verdict sites, while the CLI exit code — documented to automation as
"0 = stable" — and the stepper's per-step verdict still passed a run that had
burned nothing. Both now go through `IsCleanPass`, and the MCP tools no longer
report a zero-work run as a *definitive* instability: `inconclusive` is set for
"did no work" as well as "did not finish", with a matching explanation and a
`burn_dispatches` field, so an agent can tell "nothing was tested" from "the GPU
miscomputed". The VRAM tool had the same asymmetry against its own rounds gate.

A fifth round found three more, two of them created by earlier fixes:

- **The "did real work" guard could never fail.** The engine increments its
  dispatch counter once for a one-off reference pass before the load loop, so
  the counter is always at least 1 — and a run whose load loop never executed
  would then "verify" by comparing the reference buffer against itself, a
  guaranteed match. An MCP `run_stress` with a short window and a heavy
  intensity could return `stable: true` having executed zero burn iterations.
  Load dispatches are now counted separately, and the definition of a pass lives
  in one place — `StressProgress.IsCleanPass` — which the CLI exit code, the MCP
  tool and the stepper ask directly, while the certifier and the Stability page
  test the same two conditions in sequence so they can name which one failed.
  Having five ad-hoc copies of the rule is how three sites got it and two did not.
- **The UMA working-set change could stop the burn starting at all.** A
  structured buffer's size must be an exact multiple of its 16-byte stride;
  before the change the cap always collapsed to the aligned 64 MiB floor on
  iGPUs, but a dynamic memory budget is any number, so a small-budget UMA device
  hit `E_INVALIDARG` on buffer creation. The size is now rounded down to the
  stride — which also fixes the latent case on dedicated cards.
- **The staleness hedge covered one branch of two.** It sat below the bugcheck
  branch, so a bluescreen logged the following evening still produced a
  confident verdict naming the previous session's overclock. It now runs ahead
  of both, while the fault-stamped signatures (TDR, WHEA) — timestamped at the
  fault, not at the next boot — are correctly left alone.

A fourth round closed two:

- `vfcurve --probe` interrupted with Ctrl+C — an exit the handler added above
  created — ended as *cancelled*, which the CLI's failure test did not match, so
  it printed a normal-looking curve and exited 0. Without `--fresh` that table is
  mostly reloaded samples, so a sweep stopped at step 2 of 12 looked complete,
  and `--json` carried no outcome at all. Anything short of a completed sweep now
  fails, says how far it got, and the JSON payload carries `probe_complete` and
  the step counts.
- The V/F probe's load watchdog treated only Failed and DeviceLost as terminal,
  missing ArtifactDetected — an unstable pinned clock killing the load mid-sweep
  would have gone unreported, discarding the very evidence that clock is bad.
- Smaller: `selftest` renders the fan block's -1 sentinels as "—" like every
  other field; `fps` no longer calls a one-frame app "stopped presenting"; and an
  isolated valid sample between two dropped polls is drawn rather than being
  invisible.

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
