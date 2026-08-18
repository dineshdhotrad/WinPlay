# Changelog

All notable changes to WinPlay are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.1] — 2026-08-18

Maintenance release — functionally identical to 0.1.0. The v0.2.0 release has been
withdrawn because of audio playback regressions; its features will return in a future
release once they meet the quality bar. If you installed 0.2.0, install this build over it.

### Added

- **Support link** — a Sponsor button on the repository and a "Support WinPlay" section in
  the README, pointing to [Ko-fi](https://ko-fi.com/thedinesh).

## [0.1.0] — 2026-07-20

First public release. A native Windows AirPlay 2 sender.

### Added

- **Discovery** — bundled mDNS/DNS‑SD browser (no Bonjour); iOS Control‑Center‑style picker
  that collapses stereo pairs and multi‑room groups into single entries.
- **Audio streaming** — WASAPI loopback → lossless ALAC → encrypted RTP to any AirPlay 2
  receiver: HomePod, HomePod mini, stereo pairs, multi‑room groups, Apple TV, third‑party
  speakers.
- **Multi‑room & stereo pairs** — coordinated per‑member sessions on a built‑in
  **AirPlay‑2 PTP grandmaster clock** for sample‑accurate sync.
- **Pairing** — transient (HomePod), on‑screen **PIN** (Apple TV), and fast **pair‑verify**
  reconnects; credentials stored DPAPI‑encrypted.
- **FairPlay SAP** — clean‑room white‑box authentication, verified against golden vectors,
  enabling Apple TV screen mirroring.
- **Screen mirroring** — DXGI Desktop Duplication → D3D11 Video Processor → Media Foundation
  H.264, with **resolution negotiated from the TV**; live‑verified at 2560×1440 @ 60 fps.
- **Now Playing** metadata (title/artist/album + cover art) from the system media session.
- **Resilience** — automatic reconnect with exponential backoff.
- **Front ends** — a WinUI 3 system‑tray flyout and a scriptable CLI.

### Known limitations

- Screen mirroring is SDR H.264 only; HDR10 / Dolby Vision and HEVC are on the roadmap.
- Audio is not yet streamed alongside screen mirroring.
- Receiver identity is not pinned across sessions (same‑LAN trust model).

[0.1.1]: https://github.com/dineshdhotrad/WinPlay/releases/tag/v0.1.1
[0.1.0]: https://github.com/dineshdhotrad/WinPlay/releases/tag/v0.1.0
