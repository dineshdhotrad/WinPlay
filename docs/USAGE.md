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
| **`Win`+`Shift`+`A`** | Opens the picker from anywhere. Remappable — see [INSTALL.md](INSTALL.md#using-the-tray-menu). |
| **Click a speaker** row | Starts streaming your system audio to it (a checkmark appears); click again to stop. |
| **Volume slider** | Sets that destination's volume (0 % mutes). |
| **Apple TV → Audio / Screen / Both** | One mode at a time, never two: **Audio** (sound only), **Screen** (picture only), **Both** (picture and sound in one session on one clock). Pressing the selected mode again turns the destination off. |
| **Right‑click** the tray icon | Menu: Open WinPlay, Start with Windows, Buy me a coffee, Support on GitHub, Report an issue, Export diagnostics…, Quit WinPlay. |

When you stream, WinPlay **mutes your PC's speakers** and moves the sound to the AirPlay
device (like AirPlay on a Mac) — so there's no echo. Your speakers are restored when you
stop the last destination.

Stereo pairs and multi‑room groups appear as **single rows** (like Control Center). When
you stream to a pair or group, WinPlay opens a coordinated session to every member on a
shared timing clock, so playback stays sample‑accurate.

**Apple TV mirroring:** the first time you connect to an Apple TV, WinPlay shows a PIN
dialog and the Apple TV shows a code — enter it once and the credentials are stored
(DPAPI‑encrypted); future connections are silent.

> **Audio‑only to an Apple TV is silent** — tvOS does not render a third‑party audio‑only
> session, whichever transport it is offered on. Use **Both** if you want sound from the TV,
> and see [TROUBLESHOOTING.md](TROUBLESHOOTING.md#i-picked-audio-on-my-apple-tv-and-nothing-plays).
> For an Apple‑TV‑led home‑theatre room, WinPlay streams audio straight to the room's
> speakers instead, and that works normally.

**Now Playing:** while any destination is active, WinPlay reads your PC's current media
(Spotify, a browser tab, a music player) and pushes the title/artist/album, cover art and
playback position to the receiver's Now Playing screen automatically, and surfaces the same
track in the picker. Control also flows back: transport and volume commands the receiver
sends are applied to the app playing on your PC. (Pause/next from the *Home app* for a
HomePod use Apple's proprietary MRP protocol rather than DACP, and are not supported yet.)

### What to expect: timing

- **Sound starts about 1.8 seconds behind your PC.** That is the design: WinPlay renders at
  the same 1.75 s playout lead Apple's own senders use, so every room plays the same instant
  of audio at the same moment and the delay never drifts over a session. It is not tunable,
  and WinPlay is not a low‑latency path for gaming or for video that isn't going through the
  mirroring session.
- **The first connection to a given speaker takes 5–7 s longer** while its clock converges on
  WinPlay's. Once per speaker per app run; after that it connects immediately.
- **Screen mirroring is much tighter** — picture and (in **Both**) sound share a single
  250 ms presentation delay.

---

## The CLI

Run any command with:

```powershell
dotnet run --project tools/WinPlay.DiscoveryCli -- <command> [options]
```

or build a standalone executable and call `WinPlay.DiscoveryCli.exe <command>`.

The CLI is a protocol test harness, so a few defaults differ from the app on purpose — most
importantly, `play` uses the **realtime** transport unless you ask for `--buffered`.

### `discover` — list receivers (default command)

```powershell
winplay discover [--seconds N] [--verbose|-v]
```

Browses `_airplay._tcp` / `_raop._tcp` for `N` seconds (default 8, clamped to 1–300) and
prints both the **collapsed picker** (pairs/groups folded into single entries) and the raw
device list. `--verbose` also dumps every mDNS TXT record.

### `info` — inspect a receiver

```powershell
winplay info --to "Den TV"
```

Performs `GET /info` and pretty‑prints the receiver's property list (model, features,
display size, and the per‑stream `supportedFormats` tables the audio path negotiates from).

### `play` — stream system audio

```powershell
winplay play --to "<name>[,<name>…]" [options]
```

| Option | Default | Meaning |
|---|---|---|
| `--to` | — | One or more destination names (comma‑separated). Pairs/groups auto‑expand to a coordinated session. |
| `--buffered` | off | Use the buffered stream (type 103, AAC‑LC) instead of realtime. Members without PTP stay on realtime regardless. |
| `--minutes M` | 12 | How long to stream before tearing down. |
| `--volume dB` | −18 | Volume in dBFS (0 = full, −144 = mute). |
| `--tone` | — | Play a 440 Hz test tone instead of system audio. |
| `--lr-test` | — | Play an alternating left/right channel test (verifies stereo‑pair placement). |
| `--ntp` | — | Force NTP timing instead of PTP (for third‑party receivers). |
| `--solo` | — | Stream only to the members you named, instead of the whole pair/group they belong to. |
| `--title`, `--artist`, `--album` | — | Send Now Playing metadata after streaming starts. |

```powershell
# System audio to a single HomePod, on the buffered AAC-LC path
winplay play --to "Bedroom speaker" --buffered

# Two rooms at once, quieter, with metadata
winplay play --to "Kitchen,Office" --volume -12 --title "Weightless" --artist "Marconi Union"

# Verify a stereo pair's L/R wiring
winplay play --to "Office" --lr-test
```

`play` also runs the DACP control endpoint and advertises it over mDNS, so commands sent
back from the receiver are printed as they arrive — useful for verifying remote control
against real hardware.

### `pair` — pair an Apple TV

```powershell
winplay pair --to "Den TV" [--pin NNNN | --pin-file <path>]
```

Triggers the on‑screen PIN on the Apple TV, then completes pairing with `--pin`, or polls
`--pin-file` for up to 60 s (write the PIN into that file when it appears). Credentials are
stored so you only pair once.

### `mirror` — screen‑mirror to Apple TV

```powershell
winplay mirror --to "Den TV" [--minutes M] [--fps N] [--mbps N] [--tone]
```

| Option | Default | Meaning |
|---|---|---|
| `--minutes M` | 5 | How long to mirror. |
| `--fps N` | 30 | Capture/encode frame rate (try `--fps 60`). |
| `--mbps N` | 12 | Encoder bitrate in Mbps. Pass `--mbps 0` to let it scale automatically with resolution and frame rate. |
| `--tone` | — | Send a 440 Hz tone as the mirror session's audio instead of system audio. |

Requires a prior `pair`. The encode resolution is negotiated from the Apple TV's advertised
display and fit to your desktop — nothing is hardcoded. Unlike audio‑only sessions, the
audio carried **inside** a mirror session does play on an Apple TV.

### `trust` — manage pinned receiver identities

```powershell
winplay trust [--forget <deviceId>]
```

Lists the receivers whose identity WinPlay has pinned. `--forget` drops one, so a device
that was genuinely reset can be trusted afresh on its next connection.

### `audio` — inspect the PC's render endpoint

```powershell
winplay audio [--unmute]
```

Prints the default render endpoint's mute state and volume, plus the capture period the
audio engine actually grants. `--unmute` clears a mute left behind by an abnormal exit.

### `diagnostics` — write a bug‑report bundle

```powershell
winplay diagnostics [--out <path>]
```

Writes the same redacted bundle as the tray menu's *Export diagnostics…*, plus the list of
pinned receiver identities. Pairing credentials are never included and key material is
scrubbed before the file is written.

---

## Tips

- **Device not found?** Give discovery a moment (`--seconds 12`) — mDNS records trickle in,
  and pair/group members are resolved after the leader.
- **Names with spaces** must be quoted: `--to "Den TV"`.
- **HomePods can't mirror** — screen mirroring is Apple TV / AirPlay 2 TV only. WinPlay
  won't offer it for `AudioAccessory` devices.
- See [TROUBLESHOOTING.md](TROUBLESHOOTING.md) if something misbehaves.
