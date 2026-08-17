# WinPlay — Architecture

WinPlay is a clean‑room implementation of the AirPlay 2 **sender** side. This document maps
the codebase to the protocols it speaks.

## Projects

| Project | TFM | Role |
|---|---|---|
| `WinPlay.Core` | `net8.0` | Portable protocol engine. No UI, no GPU. Fully unit‑tested. |
| `WinPlay.Capture` | `net8.0-windows` | Media codecs: screen capture + H.264 encode (Direct3D 11 + Media Foundation), and the AAC‑LC encoder the buffered audio path uses. |
| `WinPlay.Diagnostics` | `net8.0` | Local-only logging, crash detection, and bug-report bundling. |
| `WinPlay.App` | `net8.0-windows` | WinUI 3 tray flyout. |
| `WinPlay.DiscoveryCli` | `net8.0-windows` | CLI harness. |
| `WinPlay.Core.Tests` | `net8.0` | xUnit tests. |
| `WinPlay.Capture.Tests` | `net8.0-windows` | xUnit tests for capture/encode. |
| `WinPlay.Diagnostics.Tests` | `net8.0` | xUnit tests for logging and bug-report bundling. |

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

The grandmaster's clock identity is a **MAC‑derived EUI‑64** (IEEE 1588‑2008 §7.5.2.2.2),
identical on every launch. A random per‑process identity made each run a brand‑new master to
the LAN, forcing every receiver to notice the old one vanish, re‑elect and re‑converge —
which is exactly the state in which a buffered anchor is silently discarded.

## Audio (`Core/Raop`, `Core/Audio`)

WASAPI process‑loopback capture (excluding WinPlay's own output) → resample to 44.1 kHz →
one of two transports, chosen per receiver:

| | Buffered (default in the app) | Realtime (fallback / Apple‑TV rooms) |
|---|---|---|
| Stream type | 103, over a dedicated TCP connection | 96, over RTP/UDP |
| Payload | **AAC‑LC 44.1 kHz stereo**, raw 1024‑sample access units (~192 kbit/s), Media Foundation encoder. Fallback: **ALAC 44.1/24/2** "verbatim" frames | **ALAC 44.1/16/2** "verbatim" frames, 352 samples per packet |
| Codec signalling | The RTP SSRC field is the **codec tag** on type 103 — `0x16000000` (format bit 22) for AAC‑LC, `0x13000000` (bit 19) for the ALAC fallback. A tag the receiver doesn't know means the packet is counted and dropped without ever being decrypted | Codec is implied by the stream type |
| Start of playback | One `SETRATEANCHORTIME` anchor, sent after the receiver's PTP servo has converged | Driven by 1 Hz sync packets alone |
| Requires | A PTP‑capable receiver | Nothing beyond RAOP |

Both are encrypted per packet (ChaCha20‑Poly1305, nonce derived from the sequence number); a
timing responder and a retransmit handler keep the realtime stream healthy. Stereo pairs and
groups open **one coordinated session per member** on the shared PTP clock, every member
declared in every `SETPEERS` — the leader does *not* relay audio.

Formats are never hardcoded. `GET /info` publishes a `supportedFormats` dictionary with a
separate bitmask **per stream type** (`audioStream` for realtime, `bufferStream` for
buffered), and the two sets differ; WinPlay reads the receiver's own table. If the AAC
encoder cannot be created, the buffered path degrades to ALAC; if the receiver cannot hold a
PTP clock, the whole session degrades to realtime.

## The shared timeline (`Core/Raop`, `App/Services/StreamController`)

Every session on the machine — buffered or realtime, one room or five — paces and stamps
packets against a single wall‑locked timeline, at a shared playout lead of **1.75 s**
(`BufferedLeadFrames` = 77,175 frames at 44.1 kHz). That is Apple's own captured operating
point, and the buffered anchor and the realtime sync packet's playout‑lead field both declare
it, so a realtime room and a buffered room stay phase‑locked with each other.

Two properties make cross‑room skew unrepresentable rather than merely small:

1. **Wall‑locked pacing.** Packet *k* is sent when the wall clock crosses its slot, not when
   the previous packet finished. A pump paced off its own stopwatch bakes its startup delay
   into every stamp as a permanent per‑room offset.
2. **Capture‑true stamps.** A packet is stamped with the wall position of the audio it
   carries, not the slot it is sent in. Rooms whose packets are different sizes (1024‑sample
   AAC vs 352‑sample ALAC) therefore still render the same captured instant at the same
   moment, and encoder pipeline latency — the Media Foundation AAC MFT holds about two frames
   — cannot leak into the timestamps, because each access unit carries its own queued rtp.

The consequence for users is that latency is **constant and identical everywhere**, around
1.8 s end to end, rather than low. Smaller leads were measured and each failed under ordinary
load: 0.35 s cut audibly on a busy network, and 0.75 s ran clean in steady state but collapsed
at the first disturbance — a volume `SET_PARAMETER`, another room's handshake.

The first buffered connection to a receiver waits (event‑driven, on the receiver's own
`Delay_Req` traffic, ~5–7 s in practice, 10 s cap) for its servo to converge before the anchor
is sent, because an anchor that arrives early is silently discarded and never re‑sent.
Convergence is remembered per receiver, so later connections in the same run skip the wait.

## FairPlay SAP (`Core/Fairplay`)

Screen mirroring to Apple TV requires Apple's FairPlay Session Authentication Protocol.
WinPlay's implementation is a faithful C# port of the LGPL‑3.0 **doubletake** clean‑room
white‑box (the substitution/permutation tables, the modified‑MD5 compressor, the
inverse‑AES message cipher, the SAP‑hash circuit, and the m2→m3 exchange). It is verified
byte‑for‑byte against doubletake's golden vectors — **no Apple key material is involved.**

## Mirroring (`Core/Mirror`, `WinPlay.Capture`)

1. **Handshake**: pair‑verify → encrypted RTSP → FairPlay SAP → a **two‑phase SETUP**
   (session‑level SETUP that returns the event port, then a stream‑level SETUP for the
   H.264 video stream and, in combined mode, a realtime ALAC audio stream in the *same*
   session) → TCP data channel → RECORD. The two‑phase order is what modern tvOS requires.
2. **Capture/encode** (`WinPlay.Capture`): DXGI Desktop Duplication → D3D11 Video Processor
   (BGRA→NV12 color‑convert + scale on the GPU, any vendor) → Media Foundation H.264. The
   pipeline runs in a supervised child process, so a driver fault restarts capture instead of
   killing WinPlay.
3. **Framing**: each access unit is sent as a 128‑byte header + ChaCha20‑Poly1305 payload
   (key = HKDF‑SHA512 of the pair‑verify shared secret); SPS/PPS go first as an avcC codec
   packet. Encode resolution is negotiated from the receiver's advertised display.
4. **A/V sync**: video and audio are pinned to one shared **250 ms** presentation delay, and
   every frame is timestamped from the tick at which it was *captured* — not the moment it
   reaches the sender — so pipe transit from the capture host and scheduling jitter cannot
   become sync error.

Combined Screen + Audio is deliberately a single session on a single clock. Two independent
toggles would start two sessions on two clocks, with picture and sound drifting apart by
construction, so that state is unreachable in the UI rather than merely discouraged.

**tvOS limitation:** an Apple TV does not render a *third‑party audio‑only* session. Measured
on real hardware across every combination WinPlay can offer — realtime ALAC over PTP and over
NTP, buffered ALAC over PTP, buffered AAC‑LC over PTP — the session is accepted in full and
never sounded, while the same Apple TV renders the audio inside a mirroring session. This is
the same behaviour as pyatv's long‑open
[postlund/pyatv#1666](https://github.com/postlund/pyatv/issues/1666). For an Apple‑TV‑led
home‑theatre room, `GroupSession.MembersOf` therefore streams audio **directly to the room's
speakers** and leaves the TV as the video target; buffered to those speakers is also closed —
their owner cuts rendering within seconds of a foreign grandmaster capturing their clocks —
so realtime at the shared grid lead is the one route that renders and stays.

## Testing

392 tests across three projects. `WinPlay.Core.Tests` covers the DNS wire format, feature
parsing, group collapse, the binary plist codec, SRP‑6a (RFC 5054 vector), TLV8/ALAC framing,
the PTP wire format, the broadcast audio tee, the DMAP metadata encoder, the pairing ciphers,
the session timeline and group identity rules, receiver addressing, and the **complete
FairPlay SAP golden‑vector suite**; `WinPlay.Capture.Tests` and `WinPlay.Diagnostics.Tests`
cover the capture/encode surface and the redacted bug‑report bundle (including a test
asserting that the logging stack has no network sink compiled in). Everything that can be
verified without Apple hardware is verified offline; device‑dependent behaviour is validated
against a live LAN, and audible behaviour by controlled A/B listening — clicks, dropouts and
silent renders leave no trace in the RTSP exchange.
