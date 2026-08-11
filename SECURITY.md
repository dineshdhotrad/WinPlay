# Security Policy

## Reporting a vulnerability

If you discover a security issue in WinPlay, please report it **privately** so it can be
fixed before public disclosure:

- Use GitHub's [private vulnerability reporting](https://github.com/dineshdhotrad/WinPlay/security/advisories/new)
  (Security → Report a vulnerability), or
- Email the maintainer at **dineshdhotrad99@gmail.com** with `WinPlay security` in the
  subject.

Please include steps to reproduce and the affected version. You'll get an acknowledgement
within a few days and a fix or mitigation plan.

## Scope & threat model

WinPlay is a **same‑LAN interoperability tool**. It is designed to talk to AirPlay 2
receivers on your local network.

- **Credentials** (Apple TV pairing keys) are stored **DPAPI‑encrypted** for the current
  Windows user in `%APPDATA%\WinPlay\credentials.dat`. They never leave the machine and are
  never transmitted in the clear.
- **Receiver identity is pinned across sessions** (since 0.2.0), in two tiers that we
  deliberately do not conflate:
  - **PIN‑paired receivers (Apple TV) — cryptographic proof.** Every reconnect runs HAP
    pair‑verify: the receiver must sign a fresh challenge with the private key established
    at pairing. An impostor cannot complete the handshake even if it clones every
    advertised field.
  - **Transient‑paired receivers (HomePod) — trust on first use.** Transient pairing
    establishes no long‑term identity to sign against, so WinPlay pins the Ed25519 public
    key the receiver advertises (`pk`) on first connection and **refuses to stream if it
    later changes**, in `%APPDATA%\WinPlay\receivers.dat` (DPAPI‑protected for integrity).
    This detects a substituted or spoofed device answering to a known name. Being explicit
    about the limit: it proves the identity is *unchanged*, not that the peer *holds* the
    private key. Pair receivers that support PIN pairing for full proof.
  - A device that is genuinely reset or replaced is re‑trusted **only by explicit user
    action** (`winplay trust --forget <deviceId>`, or Forget in the app) — never silently.
  - Receivers advertising no public key (some third‑party speakers) remain *unverifiable*
    and keep working, so compatibility is preserved rather than silently downgraded.
- **No telemetry.** WinPlay makes no outbound connections other than to receivers you
  select on your LAN. Diagnostics are written locally only; the logging stack has no
  network sink compiled in (enforced by a test).
- **No Apple key material** is bundled or required.

## Supported versions

Security fixes target the latest release. WinPlay is pre‑1.0; please stay current.
