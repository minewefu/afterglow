# Security policy

## Design stance

Afterglow's attack surface is deliberately small:

- **No kernel driver.** All hardware access is through the vendors' own userland driver
  libraries that ship with the graphics driver: NVIDIA's `nvml.dll` and `nvapi64.dll`, and
  on Intel `ControlLib.dll` (IGCL) and `ze_loader.dll` (Level Zero Sysman). Afterglow cannot
  be used as a bring-your-own-vulnerable-driver (BYOVD) primitive because it doesn't bring one.
- **Vendor libraries load from System32 only.** `nvml.dll`, `ControlLib.dll` and
  `ze_loader.dll` are resolved by full path through a single DllImport resolver
  (`src/Afterglow.Core/Interop/VendorLibraryResolver.cs`); `nvapi64.dll` is pinned by
  `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`. None loads by bare name.
  Default probing searches the application directory first, which in an elevated process
  would let anyone able to drop a file beside the executable get their code loaded into it.
  The resolver *throws* when a library is not in its installed location rather than
  reporting it unresolved — an unresolved result hands the lookup straight back to default
  probing — so an absent library simply fails to load and that vendor reports unavailable.
- **No telemetry, no accounts.** Afterglow uploads nothing and has no account system. It
  makes exactly one kind of network request: an **opt-in, off-by-default** update check
  against the GitHub Releases API, either at startup when you enable it in Settings or when
  you press "Check now". It sends no data beyond the request itself. With the setting off
  and the button unpressed, the app makes no network requests at all. The only external
  binary is Intel's MIT-licensed PresentMon console app, bundled with a pinned SHA-256 that
  CI verifies (`THIRD_PARTY.md`).
- **Elevation is scoped.** Admin rights are required only because the driver requires them
  for writes (clocks/fans/power) and ETW tracing; monitoring runs unelevated.
- Settings/profiles are plain JSON under `%ProgramData%\Afterglow` — no registry writes
  except the optional Run key created by the installer's startup task.

## Known trust boundary

`%ProgramData%` is writable by standard users by design, and the elevated Afterglow
process reads profiles/settings from there. This is mitigated rather than eliminated:
every value loaded from disk is schema-validated and then clamped to the driver-reported
legal range before any write reaches the GPU, so a tampered profile cannot push the
hardware beyond what the driver itself allows any tool to set. Hardening this further
(ACL-restricted state directory) is tracked for a future release; treat local standard-user
tampering as within the residual risk envelope of every tool in this category.

## Reporting a vulnerability

Please open a GitHub security advisory (Security → Report a vulnerability) or a private
report to the maintainer rather than a public issue. Include reproduction steps and impact.
You should get a response within a week; fixes for confirmed issues in the driver-write or
elevation paths are prioritized above everything else.

## Supported versions

The latest release is supported. Older releases receive fixes only for vulnerabilities that
allow privilege escalation or arbitrary code execution.
