# Changelog

## 1.0.0 — unreleased

Initial release.

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
