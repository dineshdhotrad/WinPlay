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
