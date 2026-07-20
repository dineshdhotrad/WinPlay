# WinPlay — Architecture

WinPlay is a clean‑room implementation of the AirPlay 2 **sender** side. This document maps
the codebase to the protocols it speaks.

## Projects

| Project | TFM | Role |
|---|---|---|
| `WinPlay.Core` | `net8.0` | Portable protocol engine. No UI, no GPU. Fully unit‑tested. |
| `WinPlay.Capture` | `net8.0-windows` | Screen capture + H.264 encode (Direct3D 11 + Media Foundation). |
| `WinPlay.App` | `net8.0-windows` | WinUI 3 tray flyout. |
| `WinPlay.DiscoveryCli` | `net8.0-windows` | CLI harness. |
| `WinPlay.Core.Tests` | `net8.0` | xUnit tests. |

`WinPlay.Core` deliberately targets plain `net8.0` so the protocol logic is portable and
testable without a Windows desktop. WinPlay is **100% managed .NET** — the GPU capture and
H.264 encode use Direct3D 11 and Media Foundation through managed bindings, so no native
toolchain is needed to build or run.

## Discovery (`Core/Discovery`, `Core/Dns`, `Core/Mdns`)

A bundled mDNS/DNS‑SD client (no Bonjour dependency) browses `_airplay._tcp` and
`_raop._tcp`, parses the DNS wire format (with name compression), and merges the two
service views by device id. AirPlay TXT records — `features` (a 64‑bit bitfield), `model`,
`gid`/`pgid`/`igl`/`tsid` group fields — drive the device model and the **Control‑Center
collapse** that folds stereo pairs and groups into single picker entries.

## Pairing & channel crypto (`Core/Hap`, `Core/Net`)

- **Transient pairing** (HomePods): SRP‑6a (RFC 5054, 3072‑bit, SHA‑512) with the fixed PIN
  and HKDF‑SHA512 channel keys.
- **PIN pairing / pair‑verify** (Apple TV): the full HomeKit handshake — SRP‑6a, Ed25519
  long‑term identities, X25519 pair‑verify — with credentials persisted DPAPI‑encrypted.
- **Channel encryption**: ChaCha20‑Poly1305 framing over the RTSP control/event channels.

## Timing: PTP grandmaster (`Core/Ptp`)

Multi‑room and stereo pairs require a shared clock. WinPlay runs an **AirPlay‑profile PTP
grandmaster** (IEEE 1588) on UDP 319/320 — Announce, two‑step Sync/Follow_Up, Delay_Resp,
with the Apple‑specific TLVs — so every receiver slaves to the PC's clock. Ported from
owntone's MIT‑licensed libairptp.

## Audio: RAOP realtime (`Core/Raop`, `Core/Audio`)

WASAPI loopback capture → resample to 44.1 kHz → ALAC "verbatim" frames → RTP audio,
encrypted per‑packet (ChaCha20‑Poly1305, nonce derived from the RTP sequence number). A
1 Hz sync packet maps RTP time onto the clock; a timing responder and retransmit handler
keep the stream healthy. Stereo pairs and groups open **one coordinated session per
member** on the shared PTP clock — the leader does *not* relay audio.

## FairPlay SAP (`Core/Fairplay`)

Screen mirroring to Apple TV requires Apple's FairPlay Session Authentication Protocol.
WinPlay's implementation is a faithful C# port of the LGPL‑3.0 **doubletake** clean‑room
white‑box (the substitution/permutation tables, the modified‑MD5 compressor, the
inverse‑AES message cipher, the SAP‑hash circuit, and the m2→m3 exchange). It is verified
byte‑for‑byte against doubletake's golden vectors — **no Apple key material is involved.**

## Mirroring (`Core/Mirror`, `WinPlay.Capture`)

1. **Handshake**: pair‑verify → encrypted RTSP → FairPlay SAP → a **two‑phase SETUP**
   (session‑level SETUP that returns the event port, then a stream‑level SETUP for the
   H.264 video stream) → TCP data channel → RECORD. The two‑phase order is what modern
   tvOS requires.
2. **Capture/encode** (`WinPlay.Capture`): DXGI Desktop Duplication → D3D11 Video Processor
   (BGRA→NV12 color‑convert + scale on the GPU, any vendor) → Media Foundation H.264.
3. **Framing**: each access unit is sent as a 128‑byte header + ChaCha20‑Poly1305 payload
   (key = HKDF‑SHA512 of the pair‑verify shared secret); SPS/PPS go first as an avcC codec
   packet. Encode resolution is negotiated from the receiver's advertised display.

## Testing

`WinPlay.Core.Tests` covers the DNS wire format, feature parsing, group collapse, the
binary plist codec, SRP‑6a (RFC 5054 vector), TLV8/ALAC framing, the PTP wire format, the
broadcast audio tee, the DMAP metadata encoder, the pairing ciphers, and the **complete
FairPlay SAP golden‑vector suite**. Everything that can be verified without Apple hardware
is verified offline; device‑dependent behavior is validated against a live LAN.
