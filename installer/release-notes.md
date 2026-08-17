## WinPlay 0.2.0

A native Windows 11 AirPlay 2 sender — stream system audio to HomePods, stereo pairs and
multi-room groups, mirror your screen to Apple TV, or do both at once in sync, from an
iOS-style tray picker.

**0.2.0 is the reliability, latency and security release**: WinPlay is now something you can
leave running.

### Install

- **`WinPlay-0.2.0-win-x64-Setup.exe`** — installer (per-user, no admin). Use the
  **win-arm64** build on Snapdragon / Copilot+ PCs.
- **`WinPlay-0.2.0-win-x64-portable.zip`** — portable, no install.

Installing over an existing 0.1.0 copy upgrades it in place: Setup closes the running app,
replaces it, and restarts it. Your pairings and settings are kept — nothing to redo.

### Highlights

- **Whole-home sample lock.** Every room WinPlay streams to renders the same captured instant
  at the same moment. Packets carry capture-true timestamps on one wall-locked timeline at a
  shared **1.75 s playout lead** — Apple's own captured operating point — so cross-room sync
  does not depend on which transport a room happens to ride, and the delay never drifts over
  a session. The design point is *constant* latency, identical everywhere, rather than low
  latency: expect roughly **1.8 s** end to end.
- **AirPlay 2 buffered audio.** WinPlay now negotiates the buffered stream (type 103 over
  TCP) carrying **AAC-LC 44.1 kHz stereo**, the payload shape Apple's own senders use, in
  addition to the classic realtime path. Receivers that can't hold a PTP clock are detected
  and fall back to realtime per device, at the same playout lead; you don't have to choose.
- **Screen + audio, in one session.** Apple TV's combined mode now carries the desktop and
  its sound on a single mirror session and clock, so they stay lip-synced instead of being
  two destinations racing each other.
- **Receivers can control playback.** WinPlay advertises its own DACP endpoint, so the
  transport and volume commands a HomePod or Apple TV sends back reach the app actually
  playing on your PC, and Now Playing (title, artist, art, progress) shows up on the
  receiver. (The *Home app*'s buttons for a HomePod use Apple's MRP protocol instead — see
  *Known limitations*.)
- **Global hotkey.** `Win`+`Shift`+`A` opens the picker from anywhere; remappable via
  `HKCU\Software\WinPlay\Hotkey`. The flyout also light-dismisses when you click elsewhere.
- **Receiver identity is pinned across sessions.** A device presenting a changed identity is
  refused rather than streamed to — see *Security* below.
- **Crash-isolated screen capture** — the GPU/encoder pipeline runs in a supervised child
  process, so a driver fault restarts capture instead of taking down WinPlay.
- **One-click diagnostics.** Right-click the tray icon → *Export diagnostics…* for a redacted
  bug-report bundle (pairing credentials excluded, key material scrubbed).
- A Ko-fi tray item for anyone who wants to support ongoing development.

### Fixed

- System audio could be left muted after a crash — local silencing is now derived from what's
  actually streaming and is restored on stop, exit, and after an abnormal exit.
- Multi-room echo — every member of a group now shares one start timestamp and one anchor, so
  every speaker renders the same sample at the same instant.
- Installer bugs that broke upgrade detection and let both architectures claim x64
  compatibility; Setup can now replace a running WinPlay.
- The app could fail to start entirely if launched before Wi-Fi had associated; a bad
  receiver name could take down mDNS discovery for the rest of the session; one unreachable
  speaker could stall the PTP clock for every other speaker in a group.

Full detail, including the long tail of smaller fixes, is in the
[changelog](https://github.com/dineshdhotrad/WinPlay/blob/master/CHANGELOG.md).

### Known limitations

- **Audio-only to an Apple TV is silent.** tvOS does not render a third-party audio-only
  session — WinPlay's session is accepted in full (PTP or NTP timing, ALAC or AAC-LC,
  realtime or buffered, all tested on real hardware) and never played. The same Apple TV
  plays the audio inside a *mirroring* session, so this is a tvOS policy rather than a
  handshake problem; it's the same wall as pyatv's long-open
  [postlund/pyatv#1666](https://github.com/postlund/pyatv/issues/1666). **Screen** and
  **Screen + Audio** to an Apple TV work fully, and an Apple-TV-led home-theatre room is
  streamed to directly at its speakers.
- **~1.8 s of latency, by design.** WinPlay renders at Apple's 1.75 s playout lead so every
  room is sample-locked and the delay never drifts. It is not a low-latency path, and it is
  not intended for gaming or video that isn't going through the mirroring session.
- **Buffered audio requires a PTP-capable receiver.** Devices that can't hold a PTP clock
  transparently use the realtime path instead — automatically, not as a manual toggle, and at
  the same playout lead, so they stay in sync with every other room.
- **First buffered connection to a given receiver is slower to start.** WinPlay waits for the
  receiver's PTP servo to demonstrably converge before it can safely start playback — on real
  hardware, about 5–7 seconds on first contact. Once per receiver per app run; later
  connections in the same run start immediately.
- **Releases are not yet code-signed.** Windows SmartScreen will say *"Windows protected your
  PC"* on first run — this is normal for any new, unsigned app. Click **More info → Run
  anyway**. See [docs/INSTALL.md](../docs/INSTALL.md#windows-protected-your-pc-smartscreen--what-to-expect).
- Screen mirroring is SDR H.264 only; HDR10 / Dolby Vision and HEVC remain on the roadmap.
- HomePod pause/next from the Home app use Apple's proprietary MRP protocol rather than DACP;
  WinPlay implements the DACP verbs (volume from the device works today), MRP is on the
  roadmap.

### Security

- Receiver identity is pinned across sessions: Apple TVs are verified cryptographically on
  every reconnect (HAP pair-verify); HomePods, which transient pairing leaves without a
  long-term identity, have their advertised public key pinned on first use and refuse a
  later change. See [SECURITY.md](../SECURITY.md) for exactly what each tier proves.
- Logging has no network sink compiled in, enforced by a test rather than only by policy.

100% managed .NET, no runtime prerequisites. GPL-3.0-or-later.
