# WinPlay — Usage Guide

WinPlay has two front ends that share the same protocol engine:

- **The app** (`WinPlay.App`) — a WinUI 3 system‑tray flyout for everyday use.
- **The CLI** (`winplay` / `WinPlay.DiscoveryCli`) — a scriptable tool that exposes every
  capability, handy for automation, diagnostics, and headless use.

---

## The app

```powershell
dotnet run --project src/WinPlay.App
```

WinPlay starts minimized to the system tray.

| Action | Result |
|---|---|
| **Left‑click** the tray icon | Opens the picker flyout above the taskbar. |
| **Click a speaker** row | Starts streaming your system audio to it (a checkmark appears); click again to stop. |
| **Volume slider** | Sets that destination's volume (0 % mutes). |
| **Apple TV → Audio / Mirror chips** | Choose **Audio** (system sound to the TV's speakers), **Mirror** (screen mirroring, with audio in sync), or both. |
| **Right‑click** the tray icon | Menu: Open WinPlay, Start with Windows, Support on GitHub, Report an issue, Quit. |

When you stream, WinPlay **mutes your PC's speakers** and moves the sound to the AirPlay
device (like AirPlay on a Mac) — so there's no echo. Your speakers are restored when you
stop the last destination.

Stereo pairs and multi‑room groups appear as **single rows** (like Control Center). When
you stream to a pair or group, WinPlay opens a coordinated session to every member on a
shared timing clock, so playback stays sample‑accurate.

**Apple TV mirroring:** the first time you connect to an Apple TV, WinPlay shows a PIN
dialog and the Apple TV shows a code — enter it once and the credentials are stored
(DPAPI‑encrypted); future connections are silent.

**Now Playing:** while any destination is active, WinPlay reads your PC's current media
(Spotify, a browser tab, a music player) and pushes the title/artist/album and cover art
to the receiver's Now Playing screen automatically.

---

## The CLI

Run any command with:

```powershell
dotnet run --project tools/WinPlay.DiscoveryCli -- <command> [options]
```

or build a standalone executable and call `WinPlay.DiscoveryCli.exe <command>`.

### `discover` — list receivers (default command)

```powershell
winplay discover [--seconds N] [--verbose]
```

Browses `_airplay._tcp` / `_raop._tcp` for `N` seconds (default 8) and prints both the
**collapsed picker** (pairs/groups folded into single entries) and the raw device list.
`--verbose` also dumps every mDNS TXT record.

### `info` — inspect a receiver

```powershell
winplay info --to "Living Room TV"
```

Performs `GET /info` and pretty‑prints the receiver's property list (model, features,
display size, etc.).

### `play` — stream system audio

```powershell
winplay play --to "<name>[,<name>…]" [options]
```

| Option | Default | Meaning |
|---|---|---|
| `--to` | — | One or more destination names (comma‑separated). Pairs/groups auto‑expand to a coordinated session. |
| `--minutes M` | 12 | How long to stream before tearing down. |
| `--volume dB` | −18 | Volume in dBFS (0 = full, −144 = mute). |
| `--tone` | — | Play a 440 Hz test tone instead of system audio. |
| `--lr-test` | — | Play an alternating left/right channel test (verifies stereo‑pair placement). |
| `--ntp` | — | Force NTP timing instead of PTP (for third‑party receivers). |
| `--title`, `--artist`, `--album` | — | Send Now Playing metadata after streaming starts. |

```powershell
# System audio to a single HomePod
winplay play --to "Guest Bedroom"

# Two rooms at once, quieter, with metadata
winplay play --to "Kitchen,Study" --volume -12 --title "Weightless" --artist "Marconi Union"

# Verify a stereo pair's L/R wiring
winplay play --to "Living Room" --lr-test
```

### `pair` — pair an Apple TV

```powershell
winplay pair --to "Living Room TV" [--pin NNNN | --pin-file <path>]
```

Triggers the on‑screen PIN on the Apple TV, then completes pairing with `--pin`, or polls
`--pin-file` for up to 60 s (write the PIN into that file when it appears). Credentials are
stored so you only pair once.

### `mirror` — screen‑mirror to Apple TV

```powershell
winplay mirror --to "Living Room TV" [--minutes M] [--fps N] [--mbps N]
```

| Option | Default | Meaning |
|---|---|---|
| `--minutes M` | 5 | How long to mirror. |
| `--fps N` | 30 | Capture/encode frame rate (try `--fps 60`). |
| `--mbps N` | auto | Encoder bitrate in Mbps (auto scales to resolution). |

Requires a prior `pair`. The encode resolution is negotiated from the Apple TV's advertised
display and fit to your desktop — nothing is hardcoded.

---

## Tips

- **Device not found?** Give discovery a moment (`--seconds 12`) — mDNS records trickle in,
  and pair/group members are resolved after the leader.
- **Names with spaces** must be quoted: `--to "Living Room TV"`.
- **HomePods can't mirror** — screen mirroring is Apple TV / AirPlay 2 TV only. WinPlay
  won't offer it for `AudioAccessory` devices.
- See [TROUBLESHOOTING.md](TROUBLESHOOTING.md) if something misbehaves.
