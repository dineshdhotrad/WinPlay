# WinPlay — Troubleshooting & FAQ

## No devices show up

- Make sure the PC and the receiver are on the **same subnet** (many mesh routers isolate
  bands or guest networks).
- Allow WinPlay through the **Windows Firewall** for private networks (mDNS is UDP 5353).
- Give discovery longer: `winplay discover --seconds 15`.
- Some routers block mDNS between wired and wireless clients — try both on Wi‑Fi.

## A receiver is found but streaming fails

- **`470 Connection Authorization Required`** — the receiver (usually an Apple TV) needs
  PIN pairing first. Run `winplay pair --to "<name>"` (or accept the app's PIN prompt).
- **`400 Bad Request` on SETUP** — usually a firmware quirk; re‑run. If it persists on a
  third‑party receiver, try `--ntp`.
- **Audio drops after ~30 s** — the receiver stopped getting keep‑alives, typically a
  network hiccup. WinPlay auto‑reconnects; check the diagnostics/log for the cause.

## I picked Audio on my Apple TV and nothing plays

This is a tvOS limitation, not a WinPlay bug. An Apple TV does not render a **third‑party
audio‑only** session: WinPlay's session is accepted in full and simply never sounded. Every
combination has been tested on real hardware — realtime ALAC over PTP and over NTP, buffered
ALAC, buffered AAC‑LC — with the same result, while the same Apple TV plays the audio carried
**inside** a mirroring session perfectly. pyatv has had the same issue open for years
([postlund/pyatv#1666](https://github.com/postlund/pyatv/issues/1666)).

What to do instead:

- Pick **Both** (Screen + Audio) on the Apple TV row. Picture and sound travel in one session
  on one clock and both play.
- If the Apple TV leads a **home‑theatre room** (it has paired HomePods), just pick the room:
  WinPlay streams audio straight to the room's speakers rather than through the TV, and that
  works normally.

## Audio starts about two seconds late

That is the design, not a fault. WinPlay renders at the same **1.75 s playout lead** Apple's
own senders use (~1.8 s end to end once capture and encode are counted), so every room plays
the same instant of audio at the same moment and the delay never drifts over a session.
Shorter leads were measured and each broke down under ordinary network load. There is no
setting to reduce it, and lip‑sync with video on the PC screen is not a supported use — for
video, mirror the screen instead, where picture and sound share a 250 ms budget.

## The first connection to a speaker takes several seconds

Expected on the buffered path: WinPlay waits for the receiver's PTP servo to demonstrably
converge on its clock (about 5–7 s) before starting playback, because an anchor sent earlier
is silently discarded by the receiver and never re‑sent. It happens once per receiver per app
run — connect to the same speaker again in the same session and it starts immediately.

## Only one speaker of a stereo pair plays

This is fixed in WinPlay: pairs get a coordinated session per member on a shared clock.
If you see it, confirm both members appear in `winplay discover` (the partner needs an IP);
give discovery a few extra seconds so both are resolved.

## Screen mirroring

- **Mirroring isn't offered** for HomePods — they're audio‑only. It's Apple TV / AirPlay 2
  TV only.
- **`MF_E_INVALIDMEDIATYPE`** — the encoder rejected the frame size. WinPlay negotiates and
  scales the resolution, but if you hit this, lower `--fps` or force a smaller size by
  mirroring to a receiver that advertises a display size.
- **Black screen on the TV but frames are sent** — re‑pair the Apple TV
  (`winplay pair`), then mirror again; stale credentials can decrypt‑fail silently.

## Frequently asked

**Does WinPlay need iTunes or Bonjour?**
No. Discovery, pairing, and streaming are all implemented natively.

**Which audio codec does it send?**
The buffered path (the app's default for HomePods, stereo pairs and groups) sends **AAC‑LC
44.1 kHz stereo**, the same payload shape Apple's own senders use. The realtime path — used
automatically for receivers that can't hold a PTP clock, and for Apple‑TV‑led rooms — sends
lossless **ALAC**. WinPlay picks per receiver; there is nothing to configure.

**Can I make the delay smaller?**
No. See *Audio starts about two seconds late* above — the lead is what keeps every room
sample‑locked, and it is fixed deliberately.

**Can it mirror Netflix / DRM video?**
No — FairPlay‑protected video cannot be mirrored, by design of the protocol.

**Does it send my data anywhere?**
No. WinPlay talks only to receivers on your local network. Pairing keys are stored
DPAPI‑encrypted on your PC.

**Which GPUs work for mirroring?**
Any Direct3D 11 GPU — Intel, AMD, NVIDIA, Qualcomm, MediaTek. The color‑convert path uses
the vendor‑independent D3D11 Video Processor and the universal Media Foundation encoder.

**Is HDR / Dolby Vision supported?**
Not yet — it's on the [roadmap](../README.md#roadmap). SDR mirroring is complete.

**Where are my Apple TV credentials stored?**
`%APPDATA%\WinPlay\credentials.dat`, DPAPI‑encrypted for your Windows user. Delete it to
forget all paired devices.
