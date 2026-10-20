# Overglow

Overglow is an ambient lighting app for Windows. It captures the screen (including HDR displays) or the sound that is
playing, maps the picture to the LEDs around your TV or monitor and drives them in real time, with color calibration,
smoothing and effects. It runs in the tray and is configured from a web UI that phones on the same network can open
too.

Overglow started as a fork of [HyperHDR](https://github.com/awawa-dev/HyperHDR) and is now a C# / .NET 10 app with a
React UI; see [Credits](#credits).

## Features

- **Screen sync**: DXGI desktop duplication with GPU scaling, HDR tone mapping (PQ and scRGB, 3D LUT), black border
  detection, multi-monitor capture.
- **Color**: per-channel calibration (primaries or a full 3D LUT), gamma, temperature, brightness and saturation,
  minimal backlight, power limit.
- **Smoothing**: six interpolators (Stepper, RGB, exponential, YUV, Hybrid, HybridRgb) at a configurable rate, with
  an anti-flicker filter. See [docs/smoothing.md](docs/smoothing.md).
- **Effects and music mode**: rainbow swirls, mood blobs, breath, candle, and four music effects driven by WASAPI
  loopback or a microphone.
- **Desktop app**: tray menu (lights, screen sync, music, effects, colors, pause), window with the UI, start with
  Windows, sleep and lock handling, single instance, automatic updates.
- **Control**: JSON-RPC over WebSocket and HTTP (compatible with HyperHDR clients for the commands the UI uses), MQTT
  with Home Assistant discovery, mDNS advertisement of the UI.
- **Import**: HyperHDR v17-v24 settings databases are imported on the first start.

## Supported devices

| Group | Devices |
|---|---|
| Network | WLED, DDP, E1.31 (sACN), Art-Net, Philips Hue (REST and Entertainment), Nanoleaf, LIFX, WiZ, Yeelight, Home Assistant, Zigbee2MQTT |
| USB serial | Adalight-compatible controllers (Ada, AWA with HyperSerial, LightBerry APA102), Skydimo, SP616E |
| USB HID | Robobloq SyncLight |
| Debug | File output |

Discovery (mDNS, SSDP, UDP broadcast, serial and HID enumeration) finds most of them.

## Repository layout

| Path | |
|---|---|
| [backend/](backend) | .NET 10 solution: `src/` (Core, Capture, Audio, Devices, Discovery, Mqtt, Settings, Engine, Server, Shell, Platform.Windows, App) and `tests/` (xUnit v3, golden fixtures). Details in [backend/README.md](backend/README.md). |
| [ui/](ui) | React 19 + TypeScript + Vite + Tailwind web UI, served by the app. |
| [branding/](branding) | Logo, app icon and installer images (`generate_icons.py`). |
| [packaging/](packaging) | Windows installer and update packaging (Velopack), third-party notice generator. |
| [docs/](docs) | Architecture, protocol, settings, smoothing, development guide. |

## Build, test, run, package

Requirements: .NET SDK 10.0.402 or a later 10.0 feature band (pinned by `backend/global.json`), Node.js 24, Windows
10 1809 or later for the desktop features.

```powershell
# web UI
cd ui
npm ci
npx tsc --noEmit
npm test
npm run build            # -> ui/dist, copied next to the app by the backend build

# backend
cd ../backend
dotnet build -warnaserror
dotnet test
dotnet run --project src/Overglow.App -c Release -- --capture synthetic --data-dir .local/dev

# installer (UI build, self-contained publish, Velopack package) -> artifacts/releases
cd ..
./packaging/build.ps1
```

A build run from the source tree keeps its settings in `backend/.local/data`, listens on `127.0.0.1:18290` only and
doesn't import HyperHDR settings unless told to (`--legacy-data-dir`). `overglow --help` lists every option. More in
[docs/development.md](docs/development.md) and [packaging/README.md](packaging/README.md).

## Documentation

- [Architecture](docs/architecture.md): projects, dependency rules, threading, hot path
- [JSON-RPC protocol](docs/protocol.md)
- [Settings and HyperHDR import](docs/settings.md)
- [Smoothing](docs/smoothing.md): the interpolators compared
- [Development guide](docs/development.md)
- [Backend reference](backend/README.md): every subsystem in detail

## Credits

Overglow is derived from [HyperHDR](https://github.com/awawa-dev/HyperHDR) by awawa-dev (MIT), which is derived from
[Hyperion.ng](https://github.com/hyperion-project/hyperion.ng) (MIT). The C# backend is a port of HyperHDR's C++ code;
golden fixtures exported from it keep the behavior identical where it should be. See [NOTICE.md](NOTICE.md),
[LICENSE](LICENSE) (HyperHDR's MIT License, kept as required attribution) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
