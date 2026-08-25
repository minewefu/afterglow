# Third-party components

## Intel PresentMon (console application)

- File: `ThirdParty/PresentMon/PresentMon-2.5.1-x64.exe`
- Version: 2.5.1 (pinned)
- Source: https://github.com/GameTechDev/PresentMon/releases/tag/v2.5.1
- SHA-256: `9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191`
- License: MIT (Intel Corporation) — see `ThirdParty/PresentMon/LICENSE.txt`
- Used for: FPS/frametime capture via Windows ETW present tracing (spawned as a
  child process with `--output_stdout`; no code is linked).

CI verifies the hash above before packaging a release.

## NuGet packages (shipped in release builds)

- CommunityToolkit.Mvvm (MIT) — MVVM source generators for the WPF app.
- Vortice.Direct3D11, Vortice.D3DCompiler, SharpGen.Runtime (MIT, Amer Koleci &
  contributors) — Direct3D 11 bindings used by the stress test's compute workload.
- System.Diagnostics.EventLog (MIT, .NET Foundation) — Windows event-log watcher
  for TDR detection.

Test-only: xunit, Microsoft.NET.Test.Sdk, coverlet.

## Knowledge provenance (no code copied)

The NVAPI interface IDs and structure layouts used by `Afterglow.Core` are
constants published by open-source projects (LibreHardwareMonitor, MPL-2.0;
NvAPIWrapper, MIT) and were re-declared in this codebase from those references —
see `docs/research/driver-apis.md` for the full provenance table.
