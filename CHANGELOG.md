# Changelog

All notable changes to WinPlay are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] — 2026-08-17

The reliability, latency and sync release. WinPlay moves from "working prototype" to
something you can leave running — with multi-room playback on one sample-accurate timeline.

### Added

- **AirPlay 2 buffered audio** — the buffered stream (type 103 over TCP) carrying **AAC-LC
  44.1 kHz stereo** as raw 1024-sample access units under codec tag `0x16000000`, the payload
  shape captured from Apple's own senders, anchored with `SETRATEANCHORTIME` once the
  receiver's PTP servo has measurably converged. Falls back automatically to buffered ALAC
  when no AAC encoder is available, and to the classic realtime path for receivers that
  cannot hold a PTP clock. Formats are read from each receiver's own published `bufferStream`
  table rather than hardcoded. Live-verified on HomePods and stereo pairs, including repeat
  sessions and reconnects.
- **One wall-locked multi-room timeline at a 1.75 s playout lead** — every session in every
  room, buffered or realtime, paces packets on one wall-locked timeline and stamps them with
  the **capture position of the audio they carry**, so a room sending 1024-sample AAC packets
  and a room sending 352-sample ALAC packets still render the same captured instant at the
  same moment. Realtime sync packets declare the same lead as the buffered anchor. Rooms
  render in lock-step by construction; per-room startup skew is structurally impossible.
  The lead is Apple's own captured operating point — the release optimises for *constant,
  identical* latency across the house rather than for the lowest possible latency.
- **Stable PTP grandmaster identity** — a MAC-derived EUI-64 (IEEE 1588), constant across
  launches, with per-receiver convergence memory so reconnects skip the clock-settle wait.
  A first buffered connection to a given receiver waits ~5–7 s for its servo to converge;
  every later connection to it in the same run starts immediately.
- **Multi-room topology** — stereo pairs and groups get one coordinated session per member,
  every member declared in every `SETPEERS`, all slaved to the single process-wide PTP
  grandmaster and sharing one start timestamp and one anchor. Apple-TV-led home-theatre rooms
  stream realtime **directly to the room's speakers**, which is the one route that renders
  and stays (see *Known limitations*).
- **Reverse transport control (DACP)** — receivers can drive playback on the PC. WinPlay now
  advertises its own `_dacp._tcp` control endpoint over mDNS (a full DNS-SD **responder** was
  built for this) and maps commands onto the Windows media session. Volume from a HomePod is
  applied to the AirPlay destination, as Apple's own senders do.
- **Screen + audio to an Apple TV in one session** — the desktop and its sound travel in a
  single mirror session on a single clock, both pinned to one 250 ms presentation delay and
  timestamped from the same capture instant. The TV row is a three-way selector — **Audio**,
  **Screen**, **Both** — and exactly one mode can be active: two independent toggles meant two
  sessions on two clocks, with picture and sound drifting apart by construction, so that state
  is now unreachable rather than merely discouraged.
- **Now Playing surface** in the picker — current track, artist and cover art — plus
  **progress reporting** so receivers draw their scrubber.
- **Global hotkey** `Win`+`Shift`+`A` opens the picker from anywhere; remappable via
  `HKCU\Software\WinPlay` → `Hotkey`.
- **Receiver identity pinning** — see *Security* below.
- **Diagnostics** — structured local logs (`--verbose` / `--trace`) and a one-click
  **Export diagnostics…** bundle for bug reports, with pairing credentials excluded and all
  key material redacted.
- **Crash-isolated capture** — the GPU/encoder pipeline runs in a supervised child process, so
  a driver fault restarts capture instead of killing WinPlay; it degrades to in-process
  capture rather than failing.

### Changed

- Test suite grown from 93 to **392** tests across three projects; CI runs all of them,
  enforces a zero-warning build, and compiles the installer for both architectures.
  CodeQL, Dependabot and dependency review (with a GPL-incompatible-licence deny list) added.

### Fixed

- **System audio could be left muted after a crash.** Local silencing is now *derived* from
  active reception and crash-safe: the original endpoint state is persisted before muting and
  restored on stop, on exit, and on the next launch after an abnormal exit.
- **Latency crept upward over a session** on buffered streams, because each packet was paced
  from the previous one and every stall was kept forever. Packets are now stamped from the
  wall-locked timeline, so jitter can never shift audio later: the delay sits at the shared
  lead and stays there for the life of the session.
- **Multi-room echo.** Every member of a group now shares one start timestamp and one anchor,
  computed at stream start and sent concurrently, so all speakers render the same sample at
  the same instant.
- **Audible dropouts** traced to a capture margin smaller than the driver's delivery cadence;
  margins are now sized per source.
- **A/V drift on long sessions** — resampling is drift-corrected against the wall clock.
- **Per-destination volume** is centralised on the AirPlay dB scale (0 / −30 / −144) and the
  RTSP payload is locale-independent (a comma decimal separator previously corrupted it).
- **Single instance** now redirects a second launch to the running one instead of silently
  killing it; closing the flyout can no longer terminate the app; the tray icon survives an
  Explorer restart.
- **Installer** — the `AppId` was not a valid GUID (breaking upgrade detection), both
  architectures claimed x64 compatibility, and Setup could not replace a running WinPlay.
  All fixed and verified end to end.
- **Deployment** — the app is fully self-contained and the ARM64 build now really targets
  ARM64 (the runtime identifier was hard-coded to x64).
- **The app could fail to start at all.** Building the mDNS transport throws when no interface
  will accept a multicast join, and that ran unguarded during startup — so launching before
  Wi-Fi had associated (routine, since WinPlay starts at logon) killed the app before the tray
  icon existed. Discovery is now attempted rather than required, and picked up when the network
  arrives.
- **Discovery could stop for the rest of the session.** DNS names were flattened to a dotted
  string and split back with no RFC 1035 escaping, so a receiver named `Kitchen.` produced an
  unencodable empty label that took the browse loop down with it. A single exception from any
  subscriber could likewise kill all mDNS reception. Names now round-trip losslessly, and
  neither a bad round nor a subscriber's bug can end the loop.
- **Devices that were switched off stayed in the picker** until the app restarted, then failed
  to connect. Entries now expire on a measured liveness budget.
- **Devices that published an IPv6 address before their IPv4 one became permanently unusable** —
  they were treated as fully resolved, so the address WinPlay actually dials was never requested.
- **A device that had forgotten this PC could not be recovered** when it said so at the RTSP
  status line rather than in a pairing payload; the dead credentials were kept and every later
  attempt failed identically.
- **One unreachable speaker could stop the PTP clock** for every other speaker, which then
  slowly drifted out of sync with nothing reported.
- **Mirroring failures were invisible.** MirrorSession had no way to report that it had died, so
  the toggle stayed on with nothing behind it; capture that could not start (Remote Desktop, for
  example) produced no picture and no error at all.
- **An audio dropout switched the mirroring toggle off** while the TV was still receiving the
  screen — and only the toggle, so turning it back on did nothing.
- **Stopping could leave a speaker playing.** One member's teardown failure abandoned the rest of
  a group, and destinations were torn down one at a time against a two-second shutdown deadline.
- **The speakers could stay muted** when the audio device changed mid-stream: mute and restore
  targeted whatever was default at the time rather than the device actually silenced. A failed
  restore no longer destroys the record that recovery still needs.
- **Sleep, logoff and fast user switching left the picker lying** — sessions were stopped but
  every row still read "Streaming system audio", and switching one off did nothing.
- **Idle memory was never reclaimed** because the trim threw and the failure was logged where
  nothing would show it. It now reports what it reclaims — a measured 126 MB resident set
  down to 3 MB.
- **The first time the picker opened it animated from the corner of the screen** instead of
  rising off the tray, and the system "animation effects" setting was ignored.

### Security

- **Receiver identity is pinned across sessions.** Apple TVs were already verified
  cryptographically on every reconnect (HAP pair-verify). HomePods, which transient pairing
  leaves without a long-term identity, now have their advertised Ed25519 key pinned on first
  use; a changed identity is **refused** rather than streamed to. Re-trusting is an explicit
  user action. See [SECURITY.md](SECURITY.md) for exactly what each tier proves.
- Logging has **no network sink compiled in** — enforced by a test, not just by policy.

### Known limitations

- **Audio-only to an Apple TV does not render.** tvOS does not play a third-party audio-only
  session: the session is accepted in full — PTP or NTP timing, ALAC or AAC-LC, realtime or
  buffered, each tested on real hardware — and never sounded. The same Apple TV plays the
  audio inside a *mirroring* session, so pairing, FairPlay and keys are not the gate; this is
  the same wall as pyatv's long-open
  [postlund/pyatv#1666](https://github.com/postlund/pyatv/issues/1666). Screen and
  Screen + Audio to an Apple TV work fully, and an Apple-TV-led home-theatre room is streamed
  to directly at its speakers.
- **Latency is ~1.8 s end to end, by design.** WinPlay renders at Apple's own 1.75 s playout
  lead so that every room is sample-locked and the delay never drifts; it is not a low-latency
  path. Buffered audio also requires a PTP-capable receiver — anything else transparently uses
  the realtime path at the same lead.
- **The first buffered connection to a receiver takes ~5–7 s longer** while its PTP servo
  converges. Once per receiver per app run.
- Screen mirroring is SDR H.264 only; HDR10 / Dolby Vision and HEVC remain on the roadmap.
- The Home app's pause/next buttons for a HomePod use Apple's proprietary MRP protocol rather
  than DACP; WinPlay implements the DACP verbs (volume from the device works today) and MRP
  is on the roadmap.
- Releases are not yet code-signed, so SmartScreen warns on first run — see
  [docs/INSTALL.md](docs/INSTALL.md).

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

[0.2.0]: https://github.com/dineshdhotrad/WinPlay/releases/tag/v0.2.0
[0.1.0]: https://github.com/dineshdhotrad/WinPlay/releases/tag/v0.1.0
