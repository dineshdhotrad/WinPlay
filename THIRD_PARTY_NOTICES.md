# Third-Party Notices

WinPlay is licensed under **GPL-3.0-or-later** (see [`LICENSE`](LICENSE)). This choice
is dictated by the provenance of some components below — in particular a derivative of an
LGPL-3.0 project — and by the copyleft licensing that is standard across the open-source
AirPlay ecosystem.

WinPlay contains **no Apple source code or Apple key material.** Where it interoperates
with Apple's protocols, it does so through independent implementations informed by public
protocol documentation and the open-source projects credited here.

---

## Ported / derivative code (carries the upstream license)

### doubletake — FairPlay SAP whitebox · LGPL-3.0-or-later
- Upstream: <https://github.com/omarroth/doubletake> (© omarroth)
- Used in: `src/WinPlay.Core/Fairplay/**`
- WinPlay's FairPlay SAP module (the white-box substitution/permutation tables, the
  modified-MD5 compressor, the inverse-AES message cipher, the SAP-hash circuit, and the
  m2→m3 exchange) is a faithful C# port of doubletake's clean-room Go implementation.
  As a derivative work it remains available under **LGPL-3.0-or-later**; the combined
  WinPlay work is distributed under GPL-3.0-or-later, which is compatible.

### owntone / libairptp — PTP grandmaster · MIT
- Upstream: <https://github.com/owntone/owntone-server> `src/libairptp` (© OwnTone)
- Used in: `src/WinPlay.Core/Ptp/PtpMaster.cs`
- The AirPlay-profile PTP message construction (Announce/Sync/Follow_Up/Delay_Resp,
  the Apple-specific TLVs and field values) is ported from the MIT-licensed libairptp.

---

## Consulted for protocol understanding (no code copied)

These projects were read to understand Apple's undocumented AirPlay 2 wire protocols
(RTSP/RAOP flow, HAP pairing, plist/TLV formats, the mirroring SETUP contract). WinPlay's
implementations of those parts are original expression; protocols themselves are facts.

| Project | License | What it informed |
|---|---|---|
| [owntone-server](https://github.com/owntone/owntone-server) | GPL-2.0 | RAOP/RTSP sequence, ALAC framing, sync/timing packets, group handling |
| [pyatv](https://github.com/postlund/pyatv) | MIT | HAP transient/PIN pairing and pair-verify model |
| [airplay2-receiver](https://github.com/openairplay/airplay2-receiver) | — | Mirroring two-phase SETUP contract (receiver side) |
| [UxPlay](https://github.com/FDH2/UxPlay) | GPL-3.0 | Mirroring stream key derivation, receiver behavior |
| [shairport-sync](https://github.com/mikebrady/shairport-sync) | BSD/MIT | RTP timing behavior |
| [nqptp](https://github.com/mikebrady/nqptp) | GPL-2.0 | AirPlay-2 PTP behavior |

## Runtime dependencies (NuGet)

| Package | License |
|---|---|
| NAudio.Wasapi | MIT |
| BouncyCastle.Cryptography | MIT |
| Vortice.Direct3D11 / Vortice.MediaFoundation | MIT |
| System.Security.Cryptography.ProtectedData | MIT |
| Microsoft.WindowsAppSDK | MIT |

The reference projects above are **not** redistributed with WinPlay; they were used only
during development and are not part of the published source tree.
