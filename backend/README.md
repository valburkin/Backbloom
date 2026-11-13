# Backbloom backend (.NET)

The C# / .NET 10 backend of Backbloom: screen and sound capture, the lighting engine, the LED drivers, the JSON-RPC
server, settings storage and the Windows desktop shell, in one process (`backbloom.exe`). This page is the entry point:
how the solution is organized, how to build and test it, and where each subsystem is documented.

## Contents

- [Origin and attribution](#origin-and-attribution)
- [Build and test](#build-and-test)
- [Projects](#projects)
- [Subsystem reference](#subsystem-reference)
- [Tests](#tests)

## Origin and attribution

The backend is a port of HyperHDR's C++ code. Behavior was ported file by file and is compared with
[golden fixtures](tests/fixtures/README.md) exported from the C++ code. The C++ code was deleted once this backend
reached parity; it is in the history up to commit `5f70189`. Doc comments still name the C++ source files (for example
`sources/...`); those paths refer to that commit. In the backend docs, "HyperHDR" and "C++" both mean that reference
code; deliberate deviations from it are called out where they occur and collected in the
[fixtures README](tests/fixtures/README.md#deliberate-deviations-from-hyperhdr).

LED layout parsing, image-to-LED mapping, color processing, black border detection, smoothing, the ten ported effects
and the RGBW encoders match the fixtures exactly, apart from the documented deviations.

Portions of this code are ported from [HyperHDR](https://github.com/awawa-dev/HyperHDR) (MIT, copyright awawa-dev);
the source file each part comes from is named in its doc comments. See [NOTICE.md](../NOTICE.md).

## Build and test

The SDK is pinned in [global.json](global.json) (10.0.402, `rollForward: latestFeature`). If the machine-wide SDK is
older, install this one side by side and put it first on `PATH`:

```powershell
$env:DOTNET_ROOT = '<sdk folder>'
$env:PATH = "<sdk folder>;$env:PATH"
cd backend
dotnet build -warnaserror
dotnet test
dotnet run --project src/Backbloom.App -c Release -- --port 18290
```

The [development guide](../docs/development.md) covers the UI, test filters and running a development instance next to
a real setup. Every command-line option is listed in [Running the backend](../docs/backend/running.md).

## Projects

```text
src/
  Backbloom.Core              pure logic: LED layout, image->LED mapping, color processing, black borders, smoothing,
                              priorities, the effects and the music analysis (FFT, bands) (no I/O, no threads)
  Backbloom.Capture           frame source abstraction, frame pacer, synthetic test source, capture settings, CPU
                              scaling path, HDR tone mapping table (LUT loader, default LUT)
  Backbloom.Capture.Windows   DXGI desktop duplication + Direct3D 11 scaling / HDR shader (TerraFX interop)
  Backbloom.Audio             sound capture abstraction, soundEffect settings, device sessions, conversion to
                              analysis blocks
  Backbloom.Audio.Windows     WASAPI loopback and recording capture, endpoint enumeration (TerraFX interop)
  Backbloom.Devices           LED device lifecycle (LedDeviceRunner), RGBW encoders and the 17 drivers (network,
                              serial, HID, file); driver tools for leddevice discover/getProperties/identify; the
                              guided setup's Improv Wi-Fi (USB serial) and WLED setup clients (Setup/)
  Backbloom.Discovery         mDNS client/responder, SSDP search, UDP broadcast and multicast probes, serial port and
                              HID enumeration
  Backbloom.Mqtt              the one MQTT broker connection (MQTTnet), shared by Zigbee2MQTT and the MQTT integration
  Backbloom.Settings          settings schemas + validation, SQLite store, credential store, HyperHDR import, backups
  Backbloom.Engine            orchestration: frame loop, component switches, controllers, live preview feed, running
                              effects, the lighting gate (first run, HyperHDR running)
  Backbloom.Server            Kestrel: JSON-RPC over WebSocket + POST /json-rpc, auth, UI files; the MQTT integration
  Backbloom.Shell             desktop integration, platform-neutral: power/session policy, tray menu, window
                              placement, single instance, the contracts of tray, window, autostart and system events
  Backbloom.Platform.Windows  Windows implementations of the platform interfaces: high-resolution clock, machine CPU
                              and memory load, power and session events, tray icon, WebView2 window, Run-key
                              autostart, monitor sizes (EDID)
  Backbloom.App               host: composition root (DI), command line, single instance, logging, lifetime
                              (exe: backbloom)
tests/
  Backbloom.*.Tests           xUnit v3; fixtures/ holds golden fixtures shared by the test projects
```

Dependency rules (a project may only use the ones above it in this list):

1. `Core`, `Settings`, `Discovery`, `Mqtt` (no project references)
2. `Capture` (Core), `Audio` (Core), `Devices` (Core, Discovery, Mqtt); `Capture.Windows` (Capture), `Audio.Windows`
   (Audio)
3. `Engine` (Core, Audio, Capture, Devices, Settings)
4. `Server`, `Shell` (Engine and below); `Platform.Windows` (Engine, Shell)
5. `App` (everything; the only place that creates services)

[docs/architecture.md](../docs/architecture.md) shows these layers as a diagram, together with the frame pipeline.

- **Why `Engine` exists.** The API layer needs one orchestrator to talk to (component switches, live colors,
  identify), and it can't live in `App` without the server depending on the host.
- **Platform code** lives only in `*.Windows` projects (later `*.MacOS` / `*.Linux`) behind interfaces defined in the
  projects above (`Capture.Windows` implements `IScreenCaptureSource`, `Audio.Windows` implements `IAudioCapture`;
  `App` creates them only on Windows).
- **The effects live in `Core`**: they and the music analysis they read are pure logic, verified against the C++
  fixtures by `Core.Tests`. `Audio` only gets the sound into that analysis, and `Engine` runs effects as sources.

## Subsystem reference

| Page | Covers |
|---|---|
| [Running the backend](../docs/backend/running.md) | command-line options and environment variables, console behavior, serving the UI |
| [Threading and the hot path](../docs/backend/threading-and-performance.md) | threads and their owners, settings swaps, allocation rules, baseline measurements |
| [Screen capture](../docs/backend/screen-capture.md) | monitor selection, DXGI duplication, GPU and CPU paths, HDR tone mapping, LUT, recovery |
| [Engine](../docs/backend/engine.md) | smoothing, sources and priorities, screen activity, black borders, system load |
| [Effects and music](../docs/backend/effects-and-music.md) | the effects, previews, effect mapping, running effects, music analysis, WASAPI sound capture |
| [LED controllers and the layout](../docs/backend/layout-and-controllers.md) | the `layout` model and validation, zones, profiles and Looks, power limits, multi-controller protocol |
| [LED drivers and discovery](../docs/backend/led-drivers.md) | the 17 drivers, driver contract, retries, host names, libraries, mDNS/SSDP discovery |
| [Guided hardware setup (backend)](../docs/backend/hardware-setup.md) | Improv Wi-Fi, WLED setup and strip finding, board search, Wi-Fi handling, safety rules |
| [Access, first run and network](../docs/backend/access-and-first-run.md) | local vs. remote access, the lighting gate, the first-run setup, remote access, `setup` command |
| [Desktop shell](../docs/backend/desktop-shell.md) | single instance, tray, WebView2 window, autostart, power and session events, logging, shutdown |
| [MQTT integration](../docs/backend/mqtt.md) | JSON-RPC over MQTT, availability, Home Assistant discovery |
| [Performance recording](../docs/backend/performance-recording.md) | the `performance-*.jsonl` and `crash-*.jsonl` format, GPU and LED power measurement, WLED telemetry |
| [Settings storage and HyperHDR import](../docs/backend/settings-storage.md) | sections and validation, SQLite schema, import rules, backups |
| [JSON-RPC protocol](../docs/protocol.md) | transports, messages, commands, events, differences from HyperHDR |
| [Golden fixtures](tests/fixtures/README.md) | fixture formats, how they were produced, deliberate deviations |
| [Known gaps](../docs/backend/known-gaps.md) | current limitations by area |

## Tests

- **xUnit v3** test projects named after the project they test (`Backbloom.Core.Tests`, `Backbloom.Engine.Tests`,
  ...), with shared fakes in `tests/Shared`.
- **Golden fixtures** in [tests/fixtures](tests/fixtures/README.md) pin the behavior ported from HyperHDR. They are
  frozen; change one only for a deliberate deviation and document it there.
- **Allocation tests** guard the per-frame path (see
  [Threading and the hot path](../docs/backend/threading-and-performance.md#hot-path-rules)).
- **No network listeners.** Tests never listen on the network: test hosts pass `--bind 127.0.0.1`, fake servers and
  devices bind to loopback, and UDP sockets aimed at loopback are bound to it. `tests/Shared/NetworkListenerGuard.cs`,
  linked into every test project as an assembly fixture, samples the test process's TCP listeners and UDP sockets and
  fails the assembly when one is on a non-loopback address (each would make Windows Firewall prompt once per test
  executable).
