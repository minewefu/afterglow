# Competitive landscape — sources for the README comparison

Researched August 2026 against then-current releases. Every claim in the README's
comparison table traces to this file. Corrections via issues/PRs are welcome — this is a
snapshot, and these products keep moving.

## MSI Afterburner (+ RivaTuner Statistics Server)

- Latest stable 4.6.6 (first stable "in a couple of years", ~Mar 2025); 4.6.7 betas through
  2026 add a V/F "hit map" heatmap, curve import/export, and larger curve nodes for HiDPI —
  an implicit acknowledgment of long-standing curve-editor UX complaints.
  https://www.tweaktown.com/news/108004/ · https://www.tomshardware.com/pc-components/gpus/upcoming-msi-afterburner-update-adds-heatmap-to-v-f-curve-editor-to-show-your-gpus-boosting-behavior-new-feature-shoots-for-better-overclocks-with-more-data
- **No per-game auto profiles** — only global slots with hotkeys; an entire third-party
  ecosystem exists to fill the gap (profile-switcher scripts, the closed-source NV-UV with
  "automatic game detection").
  https://forums.guru3d.com/threads/automatic-profile-switching-with-msi-afterburner.457535/ · https://github.com/christianp403-spec/NV-UV
- **RTX 50 issues**: V/F point locking (the core undervolt gesture) broken by the 50-series
  driver (reset requires reboot); OC Scanner inherits the problem.
  https://forums.guru3d.com/threads/msi-afterburner-4-6-6-final-for-nvidia-geforce-rtx-5000-series-cards.457969/
- **Kernel driver**: RTCore64.sys has a CVE history (CVE-2019-16098 exploited by ransomware
  to kill EDR; CVE-2024-1460; CVE-2024-3745) and is on the loldrivers blocklist; Riot
  Vanguard blocks it.
  https://www.loldrivers.io/drivers/e32bc3da-4db1-4858-a62c-6fbe4db6afbd/ · https://fluidattacks.com/advisories/mingus · https://fluidattacks.com/advisories/gershwin · https://forums.guru3d.com/threads/rtcore64-sys-and-valorant-vanguard.431963/
- **Fan control**: hysteresis applies to falling temperatures only; curves key off core
  temperature (no hot-spot/memory source); per-fan control not exposed stock.
  https://forums.guru3d.com/threads/afterburner-not-following-hysteresis-setting-correctly.439683/
- **CLI**: limited command-line switches exist (e.g. `-Profile1..5`, used by the
  community's Task Scheduler workarounds) — not a full scripting surface.
  https://forums.guru3d.com/threads/afterburner-change-profiles-without-gui.435267/
- **OSD/FPS**: via RTSS (injection-based; anticheat friction recurs — EAAC 2024, BF6
  "Javelin" 2025 requiring new RTSS betas; HDR washout; UWP gaps).
  https://forums.ea.com/discussions/battlefield-v-en/anti-cheat-killed-overlay-msi-afterburner-and-rivatuner-statistics-server/6806331 · https://www.msi.com/blog/battlefield-6-beta-msi-afterburner-how-to-setup-guide · https://forums.guru3d.com/threads/rtss-overlay-washed-out-colors-in-certain-games.447005/
- No built-in stress test (Kombustor is separate); no TDR auto-reset; dated skinned UI with
  documented HiDPI problems. https://forums.guru3d.com/threads/scaling-for-4k.437881/

## NVIDIA App

- Stable 11.0.8.299 (Jul 2026). Tuning = one-click **Automatic Tuning** (10–20 min scan,
  periodic re-scans) plus voltage/power/temperature/fan **target** sliders that feed the
  auto-tuner. **No manual MHz offsets, no V/F curve, no undervolting, no fan curve, no
  per-game OC profiles, no CSV logging.**
  https://www.nvidia.com/en-us/geforce/news/nvidia-app-beta-update-av1-performance-tuning/ · https://www.nvidia.com/en-us/software/nvidia-app/
- Overlay shows FPS + 1% low (no 0.1%); 2024's up-to-15% overlay perf bug was mitigated by
  defaulting filters off; 2026 TechSpot measured ~0 overhead with stats overlay.
  https://www.techpowerup.com/329960/ · https://www.techspot.com/review/3140-nvidia-app-performance/
- Auto-tune reliability complaints persist ("interrupted", toggle reverting, +0 MHz
  results). https://www.nvidia.com/en-us/geforce/forums/nvidia-app/129/546862/auto-tuning-doesnt-work/
- Telemetry without a full opt-out; no login required.
  https://www.nvidia.com/en-us/geforce/forums/nvidia-app/129/573388/ · https://www.techpowerup.com/319455/

## ASUS GPU Tweak III (strongest vendor tool)

- Actively maintained (v2.1.8.0, Aug 2026); works on any NVIDIA/AMD card; unlimited
  profiles; **Profile Connect** per-app auto-switching; VF Tuner curve editor; OSD with
  1%/0.1% lows (added Dec 2025); hysteresis setting; OC Scanner.
  https://www.asus.com/support/faq/1048435/ · https://rog.asus.com/articles/news/asus-gpu-tweak-iii-the-ultimate-tool-for-advanced-gpu-tuning/ · https://videocardz.com/pixel/asus-gpu-tweak-iii-stable-released-fixes-nvidia-driver-issues-and-adds-1-low-and-0-1-low-metrics
- Closed source; vendor-locked extras; documented complaints: settings not persisting across
  reboots, OSD crashing games until the 2.1.2.1 fix (June 2026), fan-curve application bugs
  acknowledged in ASUS's own changelogs; Igor's Lab notes vendor tools silently modify the
  V/F curve on Blackwell.
  https://rog-forum.asus.com/t5/nvidia-graphics-cards/gpu-tweak-iii-not-saving-general-settings/td-p/1114268 · https://videocardz.com/newz/asus-gpu-tweak-iii-update-fixes-game-crashes-when-closing-osd · https://www.igorslab.de/en/geforce-rtx-5090-rtx-5080-rtx-5070-ti-and-rtx-5070-significantly-faster-a-blackwell-overclocking-guide-not-just-for-dummies/
- No CLI; no throttle-reason readout; no crash watchdog.

## Other vendor tools (summary)

- **EVGA Precision X1**: frozen at 1.3.7.0 (Oct 2022) after EVGA's GPU exit; never gained
  official RTX 50 support. https://www.majorgeeks.com/files/details/evga_precision.html
- **ZOTAC FireStorm**: 3 profile slots, no OSD, no V/F editor; the most bare-bones of the
  set. https://www.zotac.com/us/news/quick-guide-firestorm-0
- **Gigabyte Control Center**: no GPU voltage control, no V/F editor, no OSD; ~858 MB
  installer; CVE-2026-4415 (CVSS 9.2 unauthenticated file write) in 2026; aggressive
  default fan curves overriding other tools.
  https://www.thefpsreview.com/2025/04/21/gigabyte-geforce-rtx-5060-ti-gaming-oc-16g-video-card-review/3/ · https://www.bleepingcomputer.com/news/security/gigabyte-control-center-vulnerable-to-arbitrary-file-write-flaw/

## Monitoring/metrics references

- **HWiNFO64** (closed, free/Pro $29-class): deepest sensors; restored RTX 50 hot spot in
  v8.52 (Aug 2026) via its own low-level driver — the kernel-access route Afterglow
  deliberately avoids; per-chip GDDR7 temps same release; free tier's shared-memory API
  limited to 12 h. https://www.hwinfo.com/forum/threads/hwinfo-v8-52-released.11310/ · https://www.hwinfo.com/licenses/
- **GPU-Z** (closed): still hides hot spot on RTX 50 as of 2.70.0 (June 2026); memory
  junction shown; kernel driver hardened in 2.70.0 after another security flaw;
  CVE-2019-7245 history. https://www.techpowerup.com/350020/ · https://www.guru3d.com/story/gpuz-driver-flaw-raises-security-concerns-with-potential-kernel-access/
- **CapFrameX** (MIT): PresentMon-based capture with P1/P0.2/P0.1 + x%-low average +
  integral metrics; OSD via RTSS (injection); bundles PresentMon exactly as Afterglow does.
  https://github.com/CXWorld/CapFrameX
- **NVIDIA hot-spot removal on RTX 50** (context for the honest-gap stance):
  https://videocardz.com/pixel/nvidia-has-removed-hot-spot-sensor-data-from-geforce-rtx-50-gpus · https://www.techpowerup.com/350705/
- **Blackwell V/F write restriction**: https://videocardz.com/newz/nv-uv-brings-one-click-undervolting-to-geforce-rtx-50-gpus
- **Frame-metric methodology** (1%/0.1% lows = average of the slowest 1%/0.1% of frames;
  percentile variants distinct): https://gamersnexus.net/site-news/2513-testing-methodology-explained-1percent-lows-and-delta-t · CapFrameX `FrametimeStatisticProvider.cs`

## What the community asks for (ranked, sourced in the notes above)

1. True undervolting with a sane editor, working on RTX 50
2. Hot spot / VRAM temperature visibility on Blackwell
3. Per-game auto profile switching
4. Trustworthy software: open source, no vulnerable kernel driver, no bloat/telemetry
5. Overlay that coexists with anticheat
6. Built-in stability validation with crash-safe apply
7. Fan control with multiple temp sources and sane hysteresis
8. Plain-language telemetry (throttle reasons)
9. Modern HiDPI UI
10. Logging/benchmark integration

Afterglow's v1 feature set was chosen directly against this list; the two items it
deliberately does not chase (kernel-sensor depth on Blackwell, injection OSD for legacy
exclusive fullscreen) are documented as non-goals with the reasoning in the README.
