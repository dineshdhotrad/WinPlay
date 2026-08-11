## WinPlay 0.2.0

A native Windows AirPlay 2 sender — stream system audio to HomePods, stereo pairs and
multi-room groups, and mirror your screen to Apple TV, from an iOS-style tray picker.

**0.2.0 is the reliability, latency and security release**: WinPlay is now something you can
leave running.

### Install

- **`WinPlay-0.2.0-win-x64-Setup.exe`** — installer (per-user, no admin). Use the
  **win-arm64** build on Snapdragon / Copilot+ PCs.
- **`WinPlay-0.2.0-win-x64-portable.zip`** — portable, no install.

Installing over an existing copy upgrades in place and keeps your pairings. On first run
Windows SmartScreen may say *"Windows protected your PC"* (expected for a new app) —
click **More info → Run anyway**.

### What's new

- **Low-latency buffered audio (~0.5 s)** — the AirPlay 2 buffered stream, with the proven
  realtime path as automatic fallback.
- **Constant latency.** Delay no longer creeps up over a session: capture is locked to an
  absolute timeline, so jitter can never shift audio later.
- **Multi-room echo fixed.** Every speaker in a group now shares one anchor and start
  timestamp, so they render the same sample at the same instant.
- **Your PC is never left muted.** Local silencing is derived from what's actually streaming
  and is restored on stop, on exit, and after a crash.
- **Receivers can control playback** — pause/next/volume from a HomePod or Apple TV reach the
  app playing on your PC (WinPlay advertises its own DACP endpoint).
- **Now Playing** in the picker, plus progress on the receiver's scrubber.
- **Win+Shift+A** opens the picker from anywhere (remappable).
- **Receiver identity is pinned** — a device presenting a changed identity is refused, not
  streamed to.
- **Crash-isolated screen capture**, one-click **redacted diagnostics bundle**, and an
  installer that can upgrade over a running WinPlay.

Full detail in the [changelog](https://github.com/dineshdhotrad/WinPlay/blob/master/CHANGELOG.md).

100% managed .NET, no runtime prerequisites. GPL-3.0-or-later.
