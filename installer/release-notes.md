## WinPlay 0.1.1

A native Windows AirPlay 2 sender — stream system audio to HomePods, stereo pairs and
multi-room groups, and mirror your screen to Apple TV, from an iOS-style tray picker.

Maintenance release — functionally identical to 0.1.0, plus a project support link
([Ko-fi ☕](https://ko-fi.com/thedinesh)). **v0.2.0 has been withdrawn** because of audio
playback regressions; its features will return in a future release once they meet the
quality bar. If you installed 0.2.0, install this build over it.

### Install

- **`WinPlay-0.1.1-win-x64-Setup.exe`** — installer (per-user, no admin). ARM64 build also attached.
- **`WinPlay-0.1.1-win-x64-portable.zip`** — portable, no install.

On first run Windows SmartScreen may say *"Windows protected your PC"* (expected for a new
app) — click **More info → Run anyway**.

### Highlights

- Lossless ALAC audio with sample-accurate multi-room sync (built-in PTP grandmaster clock).
- Local speakers muted while streaming (audio "moved" to the receiver, like AirPlay on a Mac).
- Apple TV screen mirroring with hardware H.264 encode and audio carried in the same
  session for lock-step A/V sync.
- Transient / PIN / pair-verify pairing; credentials stored DPAPI-encrypted.

100% managed .NET, no runtime prerequisites. GPL-3.0-or-later.
