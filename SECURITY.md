# Security policy

## Design stance

Afterglow's attack surface is deliberately small:

- **No kernel driver.** All hardware access is through NVIDIA's userland driver libraries
  (`nvml.dll`, `nvapi64.dll`) that ship with every GeForce driver. Afterglow cannot be used
  as a bring-your-own-vulnerable-driver (BYOVD) primitive because it doesn't bring one.
- **No network access.** Afterglow makes no network requests: no telemetry, no update
  checks, no accounts. The only external binary is Intel's MIT-licensed PresentMon console
  app, bundled with a pinned SHA-256 that CI verifies (`THIRD_PARTY.md`).
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
