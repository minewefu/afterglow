# Contributing to Afterglow

Thanks for helping build the open Afterburner alternative. A few ground rules keep the
project trustworthy:

## Ground rules

1. **No kernel drivers.** Ever. Features that require ring-0 access (RTX 50 hot spot,
   per-module VRAM temps) stay out until a userland path exists — we show an honest
   "unavailable" instead.
2. **No guessed interop.** Every NVAPI interface ID or struct layout must cite a source
   (an open-source project that ships it, or the public SDK) in
   `docs/research/driver-apis.md`, and be probed at runtime before the UI exposes it.
3. **Every write is validated.** New tuning knobs must clamp to driver-reported ranges,
   verify by readback where possible, and report per-knob results.
4. **Honest labels.** A metric's method belongs next to the metric (see the FPS page's
   1%-low definitions). Estimates and unsupported states must say so.

## Dev setup

- Windows 10/11, .NET 10 SDK. `dotnet build` / `dotnet test` from the repo root.
- `dotnet run --project src/Afterglow.App -- --demo` runs the full UI with a synthetic GPU —
  no NVIDIA hardware needed for most UI work.
- `--screenshot out.png --page <name>` renders a page off-screen (used for README shots and
  visual checks).
- Hardware verification: `afterglow-cli selftest` prints the per-capability support matrix
  for your GPU; run it (and, elevated, `set`/`reset` round-trips) before and after touching
  interop code.

## Code style

- `TreatWarningsAsErrors` is on; keep it green.
- Core stays UI-free; the WPF app consumes it through `AppServices`.
- Interop rules: blittable structs, explicit versions/sizes, `Pack = 8` for NVAPI,
  capability-gated calls that degrade to "unsupported" rather than throwing.

## Pull requests

- One feature/fix per PR, with tests for pure logic (curves, metrics, parsing).
- If a change affects hardware behavior, say what GPU/driver you verified on and paste the
  relevant `selftest` lines.
- Screenshots for UI changes (`--demo --screenshot` makes this painless).
