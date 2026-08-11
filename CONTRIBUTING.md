# Contributing to WinPlay

Thanks for your interest in WinPlay! Contributions of all kinds — bug reports, protocol
findings, device compatibility notes, code — are welcome.

## Ground rules

- **Be respectful.** This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md).
- **No Apple code or key material.** WinPlay is a clean‑room interop project. Do not paste
  Apple source, disassembly, or private keys. Protocol *facts* and behavior observed on the
  wire are fine; verbatim copyrighted code is not.
- **Mind the licenses.** WinPlay is GPL‑3.0‑or‑later. Ported code must be license‑compatible
  and credited in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). New files get an
  `// SPDX-License-Identifier: GPL-3.0-or-later` header.

## Getting set up

```powershell
git clone https://github.com/dineshdhotrad/WinPlay.git
cd WinPlay
./build.ps1                            # should be green before you start
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the pieces fit together.

## Making a change

1. **Keep `WinPlay.Core` portable** — no Windows‑UI or GPU dependencies there. Platform
   code belongs in `WinPlay.Capture` or `WinPlay.App`.
2. **Add tests.** Anything verifiable without Apple hardware (wire formats, crypto, parsing)
   should have a unit test. Golden vectors from an upstream reference are ideal.
3. **Match the house style.** Small, focused types; XML doc comments that explain *why*
   (protocol quirks), not *what*; no dead code.
4. **Run the suite** (`dotnet test`) and, where possible, verify against a real device.
5. Open a pull request describing the change and how you tested it. Note any device you
   verified against.

## Reporting bugs

Include your Windows version, GPU vendor, the receiver model (e.g. `AppleTV11,1`), and — if
it's a streaming/mirroring issue — the CLI output from the equivalent
`winplay play`/`mirror` run (it logs each protocol stage).

## Reporting security issues

Please **do not** open a public issue for vulnerabilities — see [SECURITY.md](SECURITY.md).
