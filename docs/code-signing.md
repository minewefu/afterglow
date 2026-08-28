# Code signing plan (SignPath Foundation)

Goal: signed `Afterglow-x.y.z-setup.exe` and portable binaries, eliminating the
SmartScreen warning. SignPath Foundation offers free signing for open-source
projects; the certificate is issued to *SignPath Foundation*, who become the
listed publisher.

## Eligibility check (against signpath.org conditions, retrieved 2026-08-28)

| Condition | Afterglow |
|---|---|
| OSI license, no commercial dual-licensing | MIT, single license |
| No proprietary components | All first-party code MIT; no closed components |
| Upstream binaries in packages | Bundled Intel PresentMon is upstream OSS (MIT), hash-pinned and CI-verified — explicitly permitted ("you may include unsigned binaries of upstream OSS projects in your signed packages"); Intel's own binaries are Authenticode-signed by Intel |
| Actively maintained | Yes |
| Already released | v1.0.0–v1.0.2 published with checksums |
| Functionality documented on download page | README + release notes |
| No hacking tools | GPU tuning/monitoring only |
| Privacy | No telemetry, no accounts; the update check is opt-in, off by default, documented, and calls only the GitHub Releases API |
| Announces system changes | UAC prompts for writes; installer tasks (autostart, shortcuts) are explicit opt-in checkboxes |
| Uninstallation | Inno uninstaller removes files, registry entries, and the scheduled task |

## Application draft (for the form at signpath.org/apply)

- **Project name:** Afterglow
- **Project URL:** https://github.com/minewefu/afterglow
- **License:** MIT
- **Short description:** Open-source tuning and monitoring for NVIDIA RTX GPUs —
  overclocking, measured-curve undervolting, per-fan curves, FPS metrics,
  stability testing, and crash forensics. No kernel drivers, no telemetry.
- **Why signing matters:** The app requires administrator rights (like every GPU
  tuning tool) and is currently unsigned, so users see SmartScreen warnings and
  must verify SHA-256 checksums manually. Signing closes the gap between the
  project's verified-supply-chain practices (hash-pinned third-party binary,
  CI-only releases, published checksums) and what Windows shows the user.
- **Build process:** GitHub Actions only (`.github/workflows/ci.yml`). Tagged
  releases build a self-contained portable package and an Inno Setup installer
  from a clean checkout; the bundled PresentMon binary's SHA-256 is verified in
  CI before packaging. No release artifacts are ever built on developer machines.
- **Artifacts to sign:** `Afterglow.exe`, `afterglow-cli.exe`,
  `Afterglow-x.y.z-setup.exe` (installer).
- **Team:** single maintainer (author + approver roles held by the same person,
  with GitHub 2FA enabled).

## Maintainer action items (cannot be automated)

1. Enable two-factor authentication on the GitHub account (required by SignPath's
   conditions for all team members).
2. Submit the application form at https://signpath.org/apply (HubSpot form).
3. Create the SignPath.io account when approved, also with MFA.

## CI integration (after approval)

SignPath integrates via their GitHub Action: the release job uploads the
unsigned artifacts as a signing request and downloads the signed results before
attaching them to the GitHub release. Sketch:

```yaml
- uses: signpath/github-action-submit-signing-request@v1
  with:
    api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
    organization-id: <org-id>
    project-slug: afterglow
    signing-policy-slug: release-signing
    artifact-configuration-slug: installer
    github-artifact-id: <unsigned artifact>
    wait-for-completion: true
    output-artifact-directory: signed/
```

The checksums step then hashes the *signed* artifacts, so `SHA256SUMS.txt`
matches what users download.

## Code signing policy (required by SignPath's conditions; becomes part of this repo)

- Signed artifacts are built exclusively by GitHub Actions from tagged commits
  on `main` of https://github.com/minewefu/afterglow.
- Signing requests are submitted automatically by CI and approved manually by
  the maintainer after the workflow's tests pass.
- The certificate is issued to SignPath Foundation; code signing is provided by
  [SignPath.io](https://signpath.io) with a certificate from the
  [SignPath Foundation](https://signpath.org). (This attribution line moves into
  the README once the first signed release ships.)
