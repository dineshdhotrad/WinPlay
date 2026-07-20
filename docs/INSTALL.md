# WinPlay — Install & Build

## Option A — installer (recommended)

1. Go to the [Releases](https://github.com/dineshdhotrad/WinPlay/releases) page.
2. Download **`WinPlay-<version>-win-x64-Setup.exe`** (or `win-arm64` on Snapdragon /
   Copilot+ PCs).
3. Run it. WinPlay installs **per-user — no administrator rights, no UAC prompt** — adds a
   Start Menu entry, and launches. Nothing else to preinstall (the .NET runtime and
   Windows App SDK are bundled). Uninstall any time from *Settings → Apps*.

## Option B — portable

Prefer no install? Download `WinPlay-<version>-win-x64-portable.zip`, unzip anywhere, and
run `WinPlay.App.exe`. It's fully self-contained.

WinPlay appears in your system tray. See the [usage guide](USAGE.md) to get streaming.

## "Windows protected your PC" (SmartScreen) — what to expect

Because WinPlay is a brand-new open-source app, Windows SmartScreen may show
*"Windows protected your PC"* the first time you run the downloaded installer. Click
**More info → Run anyway**.

This is **not specific to WinPlay** — Windows shows it for any newly published app that
hasn't yet built download *reputation*, even signed ones. The only ways to remove the
prompt entirely are an **EV code-signing certificate** (instant reputation) or accumulated
downloads over time; both are planned. The build pipeline is already wired to sign
releases automatically once a certificate is configured (see `.github/workflows/release.yml`
and the `SIGNING_PFX_*` secrets). WinPlay makes no outbound connections except to the
AirPlay receivers you pick on your LAN — see [SECURITY.md](../SECURITY.md).

### Using the tray menu

- **Left-click** the tray icon to open the device picker.
- **Right-click** for the menu: **Open WinPlay**, **Start with Windows** (launch at login),
  **Support on GitHub**, **Report an issue**, and **Quit**.

## Option B — build from source

### Prerequisites

- **Windows 11** (or Windows 10 21H1+).
- **.NET 8 SDK** — <https://dotnet.microsoft.com/download/dotnet/8.0>.
- Git.

WinPlay is 100% managed .NET — no native toolchain or C++ workload is required.

### Build & run

```powershell
git clone https://github.com/dineshdhotrad/WinPlay.git
cd WinPlay

# Run the app
dotnet run --project src/WinPlay.App

# …or the CLI
dotnet run --project tools/WinPlay.DiscoveryCli -- discover

# Run the test suite
dotnet test tests/WinPlay.Core.Tests
```

### Produce a standalone build

```powershell
dotnet publish src/WinPlay.App -c Release -r win-x64 --self-contained true `
  -o publish/win-x64
```

The `publish/win-x64` folder is a self‑contained WinPlay you can zip and share.

### Produce an MSIX package

WinPlay ships a `Package.appxmanifest`. To build a signed MSIX for sideloading or the
Store, open the solution in Visual Studio 2022+, set `WinPlay.App` as the startup project,
and use **Project → Package and Publish → Create App Packages**, or build with
`-p:WindowsPackageType=MSIX`.

## Firewall / network

WinPlay uses mDNS (UDP 5353), RTSP/HTTP (TCP 7000), RTP audio (UDP), and PTP timing
(UDP 319/320). On first run, allow WinPlay through the Windows Firewall for **private**
networks so it can reach your receivers. PTP ports are not privileged on Windows, so no
elevation is required.

## Uninstall

WinPlay is portable — just delete the folder. Stored pairing credentials live in
`%APPDATA%\WinPlay\credentials.dat`; delete it to forget all paired Apple TVs.
