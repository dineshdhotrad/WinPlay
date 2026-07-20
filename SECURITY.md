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
- **Receiver identity is not pinned across sessions.** Like other open‑source AirPlay
  senders, WinPlay authenticates the *pairing* but does not detect a receiver being
  impersonated by another host on the LAN. Treat the local network as trusted.
- **No telemetry.** WinPlay makes no outbound connections other than to receivers you
  select on your LAN.
- **No Apple key material** is bundled or required.

## Supported versions

Security fixes target the latest release. WinPlay is pre‑1.0; please stay current.
