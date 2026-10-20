# Overglow backend (.NET)

The C# / .NET 10 port of the Overglow backend. The C++ code in `sources/` and `include/` is the reference
implementation: behavior is ported from it file by file and compared with golden fixtures exported from it. It is
deleted once this backend reaches parity.

Status: **port steps 1-4, 5a and 6a** (engine features: smoothing, priorities, black borders, system load; see
[Engine features](#engine-features-port-step-5a)). The React UI in `ui/` connects to it, screen capture (or a synthetic
test pattern) drives the LEDs through the real mapping and color pipeline, and every LED driver of the C++ build runs on
its own device thread (see [LED drivers and discovery](#led-drivers-and-discovery)).
LED layout parsing, image to LED mapping, color processing and black border detection match fixtures exported from the
C++ reference exactly. Settings (port step 4) live in SQLite, cover every kept settings type and are imported from
HyperHDR databases. Windows screen capture with HDR tone mapping (port step 3) drives the pipeline from the
`systemGrabber` settings; see [Screen capture](#screen-capture-windows).

Portions of this code are ported from [HyperHDR](https://github.com/awawa-dev/HyperHDR) (MIT, copyright awawa-dev);
the source file each part comes from is named in its doc comments.

## Build, test, run

The SDK is pinned in `global.json` (10.0.402, `rollForward: latestFeature`). On the development machine it is installed
per user in `C:\Code\deps\dotnet` (the machine-wide SDK is older), so put it first on `PATH`:

```powershell
$env:DOTNET_ROOT = 'C:\Code\deps\dotnet'
$env:PATH = "C:\Code\deps\dotnet;$env:PATH"
cd backend
dotnet build -warnaserror
dotnet test
dotnet run --project src/Overglow.App -c Release -- --port 18290
```

Command-line options (also `OVERGLOW_<NAME>` environment variables):

| Option | Default | |
|---|---|---|
| `--port` | `18290` | Web port (HTTP, WebSocket JSON-RPC, UI files). HyperHDR keeps 8090/8092. |
| `--bind` | `127.0.0.1` | Address to listen on; use `0.0.0.0` for other devices on the network. |
| `--data-dir` | per-user `Overglow` folder; `backend/.local/data` when run from the source tree | `settings.db` (settings, controllers, credentials as hashes, instance id), `backups/`, `imported/`. See [Settings](#settings-storage-and-hyperhdr-import). |
| `--legacy-data-dir` | HyperHDR's data folder; none when run from the source tree | Where the first start looks for `db/hyperhdr.db` to import. `none` turns it off. |
| `--ui-dir` | `ui/dist` of the source tree | Built React UI to serve. |
| `--capture` | `screen` | Frame source: `screen` (DXGI desktop duplication, configured by `systemGrabber`; falls back to the test pattern on other OSes) or `synthetic` (test pattern, used by the server tests). |
| `--fps` | `60` | Rate of the synthetic test source. |
| `--lut-dirs` | data dir, app dir | Directories searched for `lut_lin_tables.3d` (or `.3d.zst`), separated by `;`; the default table is written to the first. |

### Using the UI

- Served by the backend: `cd ui && npm ci && npm run build`, then open `http://localhost:18290`.
- From a dev or preview server: `npx vite preview --port 4730` in `ui/` and open
  `http://localhost:4730/?backend=localhost:18290`.

The default admin password is `overglow` (as in the C++ build). Local clients need it for configuration unless
`network.localAdminAuth` is `false`.

## Projects

```
src/
  Overglow.Core       pure logic: LED layout, image->LED mapping, color processing, black borders (no I/O, no threads)
  Overglow.Capture    frame source abstraction, frame pacer, synthetic test source, capture settings, CPU scaling
                      path, HDR tone mapping table (LUT loader, default LUT)
  Overglow.Capture.Windows  DXGI desktop duplication + Direct3D 11 scaling / HDR shader (TerraFX interop)
  Overglow.Devices    LED device lifecycle (LedDeviceRunner), RGBW encoders and the 16 drivers (network, serial, HID,
                      file); driver tools for leddevice discover/getProperties/identify
  Overglow.Discovery  mDNS client/responder, SSDP search, UDP broadcast probes, serial port and HID enumeration
  Overglow.Mqtt       the one MQTT broker connection (MQTTnet), shared by Zigbee2MQTT and the later MQTT integration
  Overglow.Settings   settings schemas + validation, SQLite store, credential store, HyperHDR import, backups
  Overglow.Engine     orchestration: frame loop, component switches, controllers, live preview feed
  Overglow.Server     Kestrel: JSON-RPC over WebSocket + POST /json-rpc, auth, UI files
  Overglow.Platform.Windows  Windows implementations of the engine's platform interfaces (high-resolution clock,
                      machine CPU and memory load)
  Overglow.App        host: composition root (DI), configuration, logging (exe: overglow)
tests/
  Overglow.*.Tests    xUnit v3; fixtures/ holds golden fixtures shared by the test projects
```

Dependency rules (a project may only use the ones above it in this list):

1. `Core`, `Settings`, `Discovery`, `Mqtt` (no project references)
2. `Capture` (Core), `Devices` (Core, Discovery, Mqtt); `Capture.Windows` (Capture)
3. `Engine` (Core, Capture, Devices, Settings); `Platform.Windows` (Engine)
4. `Server` (Engine and below)
5. `App` (everything; the only place that creates services)

`Overglow.Engine` is an addition to the plan's layout: the API layer needs one orchestrator to talk to
(component switches, live colors, identify), and it can't live in `App` without the server depending on the host.

Platform code lives only in `*.Windows` / `*.MacOS` / `*.Linux` projects behind interfaces defined in the projects
above (`Capture.Windows` implements `IScreenCaptureSource`; `App` creates it only on Windows).

## Threading model

| Thread | Owner | Does |
|---|---|---|
| Frame loop (1, above-normal priority) | `LightingPipeline` | capture -> map -> process -> hand frames to controllers and the preview feed |
| Smoothing (1, above-normal priority) | `OutputStage` | while smoothing runs: one tick per update interval (interpolate, anti-flicker filter, hand the frame to the controllers and the preview); idle otherwise |
| One per controller | `LedDeviceRunner` | open (with retries), send the newest frame, refresh, blink, close |
| Thread pool | ASP.NET Core | JSON-RPC requests (in order per connection), event pushes, LED stream timers |

- The frame loop never waits on a controller or a client. `LedDeviceRunner.Submit` copies the colors into a
  preallocated buffer; if the device thread hasn't sent the previous frame yet, that frame is dropped (counted in
  the stats), never queued.
- Settings changes arrive on request threads. They build new immutable objects (`PipelineConfig`: LED map options +
  `ColorProcessor`) and swap them in with one reference write; the frame loop picks them up on its next frame.
- Controllers are recreated when `device` or `leds` change; the old one is switched off (black frame, power off) first.
- Events (`*-update`) are raised on the thread that caused the change; each WebSocket session serializes them into
  its own outbound queue, drained by one send loop.

## Hot-path rules

The frame loop and `LedDeviceRunner.Submit` must not allocate in steady state (`GC.GetAllocatedBytesForCurrentThread`
tests enforce it in Core, Devices, Engine, Capture and Capture.Windows):

- buffers are sized when the LED count or frame size changes, not per frame;
- `ImageToLedMap` precomputes pixel offsets per LED into flat arrays;
- `ColorProcessor` precomputes its lookup tables and is immutable;
- `BlackBorderDetector` samples a few lines of the frame in place; `BlackBorderProcessor` only keeps counters;
- logging only through `[LoggerMessage]` methods and never per frame;
- drivers send with preallocated packet buffers over connected UDP sockets (`Socket.Send(ReadOnlySpan<byte>)`); the
  streaming drivers (WLED, DDP, E1.31, Art-Net, Adalight, Skydimo, SP616E, SyncLight, Nanoleaf, LIFX, Hue
  Entertainment) and the RGBW encoders are allocation-free per frame. The JSON/REST lamp drivers (WiZ, Yeelight, Home
  Assistant, Zigbee2MQTT, Hue REST) build small strings per frame on their device thread, at most a few times a second.

Measured with the owner's 168-LED layout, the synthetic source at 60 fps (512x288 frames, 155,684 sampled pixels per
frame) and the file driver writing to the null device (Release build, 24 logical cores): about 1.3 % of one core
without clients and 2.1 % with the UI's live preview connected; working set about 62 MB (22 MB private).

The synthetic source paces itself by absolute deadlines because the default Windows timer resolution is 15.6 ms;
the average rate is exact, single frames jitter by up to one timer tick. Screen capture uses the same pacer for its
`fps` limit and then takes the newest desktop image DXGI has.

## Screen capture (Windows)

Port of the DX grabber (`DxGrabber`, `Grabber`, `SystemWrapper`, `LutLoader`, `DefaultLut`). `DxgiScreenSource` runs
entirely on the frame loop thread (D3D11's immediate context is single-threaded): it initializes lazily, retries after
failures and rebuilds the duplication when it is lost.

| | |
|---|---|
| Monitors | `MonitorEnumerator`: DXGI outputs plus the display configuration API. Setting values: `auto` (primary monitor; HyperHDR took the first sorted entry, which could be a multi-monitor one), `\\.\DISPLAY5\|NVIDIA GeForce RTX 5080` (HyperHDR's id, kept so imported settings work and the UI can show it), the stable monitor device path (or `edid:manufacturer:product:connector` when the driver reports none), or `MULTI-MONITOR\|adapter` (all monitors of an adapter side by side, ordered by `reorder_displays` as `std::next_permutation` did). |
| Duplication | `IDXGIOutput6::DuplicateOutput1` (the capture thread is made per-monitor DPI aware, otherwise it fails with `DXGI_ERROR_UNSUPPORTED`). HDR is detected per monitor (`DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020`); with hardware on and `monitor_nits` > 0 the desktop is duplicated as FP16 scRGB (10-bit PQ as a fallback), else as 8-bit BGRA, which Windows converts to SDR. |
| GPU path (`hardware`) | `GpuScaler`: the acquired texture is copied into an own texture (the frame is released at once), `Shaders/Downscale.hlsl` averages each `divide` x `divide` block of the cropped area (bilinear taps between texels, up to 8 x 8 per pixel) into a small BGRA target, then one staging readback of that size. Modes: scRGB scaled by 80 / `monitor_nits` + BT.709 OETF (the C++ HDR shader, reconstructed from its bytecode), PQ/BT.2020 -> BT.709, the LUT as a 256^3 RGBA8 3D texture read with exact texel loads (8-bit captures with `hdrToneMapping`), or plain SDR. The shader only runs when the image or the mode changed. |
| CPU path | `CpuScaler` + `BgraDownscaler` (port of `decodeBGRA`): full-size 8-bit readback, every `divide`-th pixel, LUT on the CPU. Used when `hardware` is off or the shaders can't be created. |
| Scaling, crop | `CaptureRegion`: the smallest whole factor that makes the cropped width fit `videoMode` (as `getTargetSystemFrameDimension`). Crops are in screen pixels on both paths (the C++ GPU path cropped the scaled image) and are ignored when they leave 16 pixels or less. |
| Pacing, latency | `fps` via `FramePacer`; then `AcquireNextFrame(0)` returns the newest desktop image (no new image: the last one is delivered again from the staging texture; only the pointer moved: no GPU work). Until the first image after (re)start it waits up to 100 ms. |
| Recovery | `DXGI_ERROR_ACCESS_LOST` / invalid call / device removed (display mode or HDR change, desktop switch, driver reset): everything is released and rebuilt 100 ms later. `E_ACCESSDENIED` (secure desktop: UAC, lock screen): retried every second; the thread attaches to the input desktop (`OpenInputDesktop` + `SetThreadDesktop`) so a service can follow it. Missing monitor: retried every 5 s. A retry waits instead of spinning; the same problem is logged once. |
| LUT | `LutLoader`: first existing `lut_lin_tables.3d` or `.3d.zst` wins; one table or the legacy three-table file (first table used). `LutProvider` loads in the background on first use (only when an 8-bit capture has `hdrToneMapping`) and otherwise builds the default table (`DefaultLut`, bit-identical to C++, ~0.3 s on 24 cores) and saves it to the first LUT directory. |
| Settings | `systemGrabber` (schema in `Overglow.Settings`); `App` reconfigures the source on every change of that section, the next frame restarts the capture. |

Interop: [TerraFX.Interop.Windows](https://github.com/terrafx/terrafx.interop.windows) (MIT). It is a 1:1 projection
of the Windows SDK headers as blittable structs and function pointers, so the port reads like the C++ code, COM calls
allocate nothing (no runtime-callable wrappers, unlike Vortice's class wrappers, which allocate per acquired frame) and
it is trimming/AOT compatible with no source generator configuration (CsWin32 would need one per API and its COM
wrappers marshal by default). Unused parts of the large assembly are trimmed when published.

Shaders: `Shaders/Downscale.hlsl` is the source; the bytecode (`Downscale.vs.cso`, `Downscale.ps.cso`, embedded) is
checked in, so building needs no Windows SDK. `Shaders.targets` recompiles it with the SDK's `fxc` (vs_5_0/ps_5_0,
`/O3 /WX`) when the HLSL is newer; fxc output is deterministic. The resources are marked `WithCulture="false"`
(MSBuild would otherwise treat `Downscale.ps.cso` as a Pashto satellite resource).

Measured on the RTX 5080 with a 3440x1440 desktop texture scaled to 491x205 (Release, 300 frames): GPU path 0.21 ms
per frame for FP16 HDR and 0.13 ms for 8-bit (copy, shader, readback; 0.31 / 0.21 ms CPU time), CPU path 0.78 ms; no
allocations on either path. Waiting for a monitor that delivers no frames costs 0.1-0.3 % of one core.

## Engine features (port step 5a)

### Smoothing

Port of `InfiniteSmoothing` and the six interpolators (`Overglow.Core/Smoothing`), bit-identical to the C++ ones
(`fixtures/smoothing`, tolerance 0). C++'s Stepper and Rgb interpolators differ only in target blending, so
`StepperInterpolator` is the `RgbInterpolator` without it.

| Setting (`smoothing`) | Effect |
|---|---|
| `enable` | initial state of the `SMOOTHING` component (changing the setting switches it) |
| `type` | Stepper, RgbInterpolator, YuvInterpolator, HybridInterpolator, HybridRgbInterpolator, ExponentialInterpolator |
| `time_ms`, `smoothingFactor`, `stiffness`, `damping`, `y_limit` | interpolator parameters, as in C++ |
| `updateFrequency` | ticks per second = frames per second sent to every controller while smoothing runs (interval round(max(1000 / max(f, 20), 5)) ms) |
| `continuousOutput` | off: after a transition three more frames, then one per second |
| `antiFlickeringFilter` | an LED keeps its value until it changes by about half an 8-bit step (also without smoothing, as in C++) |

Design (`OutputStage`, `Smoother`): the frame loop hands each processed frame to the smoother (one lock, a copy into
preallocated buffers) and returns; the smoothing thread ticks on absolute deadlines of the injected `IClock` and sends
the result to every controller and the live preview. Without smoothing (disabled, or the visible source's
configuration is "direct") frames go out from the frame loop. One smoother feeds all controllers (HyperHDR ran one per
instance). The configuration is selected when the visible source or the settings change, never per frame. On Windows
`WindowsClock` waits with a high-resolution waitable timer (wake-ups within ~0.5 ms, 50.0 ticks/s measured) without
raising the system timer resolution, which Qt's precise timer (`timeSetEvent`, 1 ms) did system-wide. Time stamps stay
integers (C++ rounded them through `float`, which loses millisecond precision after 4.6 hours of uptime). After a
smoothing restart a static color is drawn again, so the next change still fades (HyperHDR re-ran its LED update).

Measured (Release, `SmoothingProfile`, explicit test): owner's 168 LEDs, synthetic source at 60 fps, four file
controllers to the null device: 1.25 % of one core without smoothing, 1.72 % with HybridRgb at 50 Hz. Neither path
allocates (`Pipeline_SteadyStateDoesNotAllocate`, `SmoothingTicks_DoNotAllocate`).

### Sources and priorities

Port of `Muxer` (`Overglow.Core/Priorities`) behind `PriorityManager` (Engine: lock, 250 ms timeout timer, events). The
lowest priority number among the active sources is visible, unless one was selected manually (`sourceselect`); 255 is
"nothing" (black, and the controllers are switched off, as HyperHDR did; the `LEDDEVICE` switch keeps the user's
choice). The screen registers at `systemControl.systemInstancePriority` (245) while `ALL` and `SYSTEMGRABBER` are on.
`color` registers a static color (priority 1..253, optional `duration` ms, origin `<origin>@<peer>`); static colors
skip the color settings as in HyperHDR, are drawn once per change and then smoothed. `clear` takes -1 (all colors and
effects) or 1..253, `clearall` = -1. `serverinfo.priorities` lists the visible source first with `priority`,
`componentId`, `origin`, `owner`, `active`, `visible`, `duration_ms` and, for colors, `value: { RGB, HSL }`;
`priorities_autoselect` and `activeLedColor` as in HyperHDR. Effects and music (5b) implement `ILightSource` (render
linear LED colors on the frame loop while visible) and register with `Sources.Register` and `AddSmoothingConfig`.

### Black borders

`BlackBorderProcessor` runs on screen frames while the `BLACKBORDER` component is on (initially
`blackborderdetector.enable`; settings changes apply at once); the detected border is left out of the LED mapping (the
map is rebuilt only when the border changes). Off: no border. Unlike HyperHDR the border survives a frame size change.

### System load

`performance-update` reports the whole computer's CPU and memory (the UI's "whole computer" tiles), from
`ISystemMetrics`: `WindowsSystemMetrics` (`GetSystemTimes`, `GlobalMemoryStatusEx`; HyperHDR used the PDH processor
counter, the same quantity), else this process's share (`ProcessMetrics`). LED updates/s is what the controllers
actually sent (the smoothing rate while smoothing runs).

## LED drivers and discovery

Every driver of the C++ build is ported (sources/led-drivers); the settings keys and schemas are the C++ ones
(`Overglow.Devices/Schemas`, reformatted), so imported configurations keep working. A driver only speaks its protocol
(`ILedDriver`: open, write a frame, close); `LedDeviceRunner` does the lifecycle once for all of them.

| Type | Group | Transport | Notes |
|---|---|---|---|
| `wled` | network | REST + UDP | DRGB/DNRGB; discovery: mDNS `_wled._tcp` |
| `ddp` | network | UDP 4048 | RGB, classic RGBW (`rgbw` + `white_algorithm`), RGBW by the Infinite Color Engine |
| `udpe131` | network | UDP 5568 | E1.31/sACN, 512 or 510 channels per universe, CID from the settings or random |
| `udpartnet` | network | UDP 6454 | ArtDmx, channels per fixture, splitting, RGBW (ICE) |
| `philipshue` | network | REST (v1 http, v2 https) + DTLS 1.2 PSK UDP 2100 | REST lights, Entertainment v1 groups and v2 configurations; discovery: mDNS `_hue._tcp`, else SSDP "IpBridge"; `getProperties` reads the bridge, with `pair: true` it creates a username + client key |
| `nanoleaf` | network | REST 16021 + UDP | panel order (top/bottom, left/right, start panel), External Control v2; discovery: SSDP |
| `lifx` | network | UDP 56700 | SetColor (HSBK, unrounded colors) per lamp, SetPower; discovery: GetService broadcast |
| `wiz` | network | UDP 38899 | JSON setPilot per lamp; discovery: getPilot broadcast |
| `yeelight` | network | TCP 55443 + music mode | the lamp connects back to our music mode server; command quota wait; discovery: SSDP port 1982 |
| `home_assistant` | network | REST | `light.turn_on` per entity (RGB or hue/saturation), constant brightness, state restore; discovery: mDNS `_home-assistant._tcp` |
| `zigbee2mqtt` | network | MQTT | `zigbee2mqtt/<lamp>/set`; lamps switched off on shutdown; discovery: the bridge's retained device list |
| `adalight` | serial | System.IO.Ports | Ada, AWA (Fletcher checksum, white calibration, RGBW by ICE), LightBerry APA102; "auto" port, ESP/Pico handshake and goodbye |
| `skydimo`, `sp616e` | serial | System.IO.Ports | |
| `synclight` | HID | HidSharp | Robobloq SyncLight: RB/SC frames in 65-byte reports, per-segment merging, LED count read from the controller |
| `file` | debug | file | bytes, deep colors (`infiniteColorEngine`) or ICE RGBW lines |

**Driver contract additions.** `IFloatColorDriver` takes the processed colors before 8-bit rounding (LIFX, Hue
Entertainment, and DDP/Art-Net/Adalight/file with RGBW by the Infinite Color Engine; port of `writeInfiniteColors`); the
runner passes them in RGB order with the refresh interval. `ILedDriver.EffectiveRefreshInterval` lets a protocol set
the resend rate (Hue Entertainment 20 ms, Hue REST 100 ms per light, none for Yeelight / Nanoleaf). A driver that loses
its controller while streaming (serial port unplugged, too many write timeouts, all Yeelights in error, HTTP errors)
throws `DeviceLostException`; the runner goes to `WaitingToRetry` and reopens it with the driver's retry policy (C++
`setInError` + `setupRetry`). JSON/REST drivers write their JSON with sorted keys, as `QJsonDocument` did.

**RGBW.** `RgbwConverter` (classic 8-bit algorithms, HyperSerial calibration tables) and `InfiniteRgbwEncoder`
(stateful, dithered) reproduce the C++ float operations in the same order and match the exported fixtures exactly.

**Libraries** (all permissive, maintained, pinned in `Directory.Packages.props`):

| Package | License | Used for | Why |
|---|---|---|---|
| BouncyCastle.Cryptography 2.7 | MIT | DTLS 1.2 PSK client (Hue Entertainment) | .NET has no DTLS; BouncyCastle is the standard managed implementation and needs no native OpenSSL |
| MQTTnet 5.2 (+ MQTTnet.Server in tests) | MIT | `Overglow.Mqtt.MqttConnection` | the .NET Foundation MQTT client; tests run a real broker in process |
| HidSharp 2.6 | Apache-2.0 | HID enumeration and reports, serial port names/USB ids | managed, cross-platform (hid.dll, hidraw, IOKit), so no platform project is needed; TerraFX would cover Windows only and need a Linux/macOS port later |
| System.IO.Ports 10.0 | MIT | serial I/O | the platform's serial API |

**Discovery** (`Overglow.Discovery`). Passive: it only sends queries (mDNS PTR, SSDP M-SEARCH, the LIFX/WiZ broadcast
probes) and lists what answers; nothing found is configured or contacted by the discovery itself.
- mDNS is implemented here (about 900 lines: DNS message codec, cache with TTLs and goodbyes, responder) instead of using a
  library: the maintained .NET options are browse-only (Zeroconf) or unmaintained since 2019 (Makaretu.Dns.Multicast),
  and the protocol subset needed is small and fully testable without sockets (`IMdnsTransport`). The socket shares
  UDP 5353 with the OS responder (address reuse, 224.0.0.251 joined per interface); if that fails, queries go out as
  RFC 6762 legacy unicast queries and nothing is advertised. Own addresses are never reported.
- The web UI is advertised as `<host>:<port>._overglow-http._tcp.local` (as the C++ build) when the server listens on
  the network (`--bind` other than loopback); with the default 127.0.0.1 it isn't.
- SSDP: one M-SEARCH (C++ message, MX 1) and every answer within 3 s, headers parsed like `SSDPDiscover`.
- Serial ports: HidSharp's serial device list (friendly name, USB ids from the Windows device path) plus
  `SerialPort.GetPortNames()`; HID devices: HidSharp with the top-level usage page/usage.

`leddevice discover` replies `{ ledDeviceType, devices: [...] }` with the C++ fields of each driver plus `value` (what
to put into the address/port setting) and `name` for the UI; `getProperties` replies `{ properties }`; `identify` with
`ledDeviceType: "blink"` blinks an LED, otherwise it runs the driver's identify (Hue alert, WiZ pulse, LIFX blue,
Yeelight color flow, Nanoleaf, Zigbee2MQTT).

Deliberate differences from the C++ drivers:

1. Art-Net: the datagram carries the even data length the header announces (C++ sent one byte less for odd lengths),
   and the RGB sequence never sends 0 (it wrapped to 0 once per 255 frames).
2. E1.31: the universe is 16-bit (C++ stored it in a byte).
3. Nanoleaf: the stream port from the Light Panels' answer is used as is (C++ truncated it to a byte).
4. Yeelight reads the lamps' state only with "restore original state" (C++ read it on every switch-on, costing the
   command quota).
5. Serial drivers send three black frames on close (one by the runner, two by the driver), as C++; HID SyncLight one.
6. The LAN HTTPS client accepts unvalidated certificates only for private, link-local and `.local` hosts (C++
   ignored TLS errors for every host).
7. Discovery results carry `value` / `name` for every driver (the C++ shapes are kept alongside).

## JSON-RPC protocol

Compatible with the C++ backend for what the UI uses (`ui/src/api/rpcApi.ts`): requests
`{command, subcommand?, tan, ...}`, replies `{command, tan, success, info?, error?}`, events `{command: "*-update", data}`.

| Command | Status |
|---|---|
| `authorize` tokenRequired, adminRequired, newPasswordRequired, login (password / token), logout, newPassword, createToken, getTokenList, deleteToken | done |
| `sysinfo`, `serverinfo` (+ subscriptions), `performance-counters` | done |
| `config` getschema, getconfig, setconfig | done (every kept settings type, see [Settings](#settings-storage-and-hyperhdr-import)) |
| `save-db`, `load-db` | done (backups in the C++ export format; HyperHDR backups are accepted) |
| `componentstate` ALL, SMOOTHING, BLACKBORDER, SYSTEMGRABBER, LEDDEVICE | done |
| `color`, `clear`, `clearall`, `sourceselect` | done (see [Sources and priorities](#sources-and-priorities)) |
| `adjustment`, `ledcolors` ledstream-start/stop, `leddevice` identify/discover/getProperties, `logging`, `instance` switchTo | done |
| `effect`, `image`, imagestream | not yet ("Command not implemented"; effects come with port step 5b) |

Deliberate differences from the C++ backend (its bug backlog):

1. `setconfig` rejects invalid values with `errors: [{path, message}]` and stores nothing; it never resets values to
   defaults. Unknown sections are rejected too.
2. `adjustment` is stored in `color.channelAdjustment` (so its `settings-update` is true); it needs admin rights.
3. `serverinfo` always returns the info, also when subscribing to `performance-update`.
4. `logging start` replies first; the history follows as a `logging-update` event without the request's tan.
5. Settings can be saved while the lights are off.
6. `load-db` restores while running: the settings are migrated like an import, the current ones are saved to
   `backups/` first, the reply lists what the migration changed, and the pipeline and controllers pick the new values
   up through `settings-update` (HyperHDR quit the process). Sections the backup doesn't contain get their defaults;
   an absent `layout` is not announced by an event, so clients reload the config after a restore.
7. `layout` has no default: `getconfig` omits it until a client saves one.

Also: `logout` returns the connection to the rights it had before signing in, and the identify blink is applied
before the controller's color order (so it is really red, green, blue on GRB strips).

## Settings storage and HyperHDR import

**Sections and validation.** Every settings type the C++ build kept is a section: `general`, `logger`, `device`,
`color`, `smoothing`, `systemGrabber`, `blackborderdetector`, `foregroundEffect`, `backgroundEffect`, `soundEffect`,
`webConfig`, `effects`, `systemControl`, `network`, `ledConfig`, `leds`, `mqtt`, `layout`. Their schemas are the C++
files (`sources/base/schema`, copied unchanged into `Overglow.Settings/Schemas`), the LED driver schemas come from
`Overglow.Devices`, and defaults exist only there. The in-house validator stays: the schemas use the draft-03
`required: true` form that the standard validators don't support. `setconfig` uses it strictly (reject, per-field
errors); only imports and stored data from an older schema use its repair mode (clamp numbers, reset values of the wrong
type or outside an enum to their default, drop unknown fields, report every fix).

**Database.** `settings.db` (Microsoft.Data.Sqlite), one connection per process, every use serialized; reads come from
a validated in-memory copy, every update is one transaction written before the copy changes. `PRAGMA application_id`
marks the file, `PRAGMA user_version` is the schema version: `SettingsDatabase.Migrations` lists the forward steps, each
applied once in its own transaction; a file from a newer version is refused, never rewritten. Schema 1:

| Table | Holds |
|---|---|
| `settings (section, value, updated_at)` | the pipeline's sections (JSON text) |
| `controllers (id, name, enabled)`, `controller_settings (controller_id, section, value, updated_at)` | the LED controllers and their `device` |
| `admin_password`, `api_tokens` | hashes with salt, algorithm and iterations |
| `meta (key, value)` | instance id, first-start and import markers |

Rows of sections this version doesn't know (written by a newer one) are kept untouched.

**Instances -> one pipeline.** HyperHDR ran a complete pipeline per instance; this backend runs one pipeline that feeds
many controllers:

| HyperHDR | Overglow .NET |
|---|---|
| global types (`general`, `logger`, `network`, `webConfig`, `mqtt`, `systemGrabber`, `soundEffect`, `layout`) | pipeline sections |
| per-instance types of the first instance (`leds`, `ledConfig`, `color`, `smoothing`, `blackborderdetector`, `foregroundEffect`, `backgroundEffect`, `effects`, `systemControl`) | pipeline sections |
| `device` of instance N (in instance order), name and enabled flag from `instances` | controller N; controller 0 is the `device` of `getconfig`/`setconfig` |
| per-instance settings of further instances | not imported (reported) |

The engine drives controller 0 today; the other controllers are stored for the multi-controller work.

**Import** (`SettingsImporter`, `LegacyMigrations`, ported from InstanceConfig, SettingsMigration, LegacyMigration and
AccessManager/AuthTable): reads HyperHDR v17 to 24-beta databases (`hyperhdr_instance` column, user `Hyperhdr`) and
Overglow C++ ones (`instance` column, user `admin`); the source is copied to `imported/` and only the copy is opened.
Rules, all idempotent and logged one line each (never with secrets):

- format upgrades by `general.version` (HyperHDR upgradeDB up to 6: backlight threshold steps, pause while locked);
- removed settings types are dropped: proto/flatbuffers/raw UDP/boblight/JSON servers, forwarder, the capture-card stack
  (`videoGrabber`, `videoControl`, `videoDetection`, `automaticToneMapping`), `grabberV4L2`; unknown types too;
- CEC (`systemControl.cecControl`) and signal detection options (`systemGrabber`) are removed;
- removed effects map to the kept ones (e.g. "Knight rider" -> "Breath", music color variants -> MULTI COLOR);
- a device type that this build doesn't have becomes the `file` driver (no output), keeping color order and refresh
  time; the copy in `imported/` keeps the original for drivers ported later;
- HyperHDR's factory name "My HyperHDR Config" becomes "My Overglow Config"; an empty C++ `layout` is left absent;
- every section is then repaired against its schema;
- credentials: the admin password hash (salted SHA-512) and API token hashes are imported as `LegacySha512` and keep
  working; `AuthService` replaces them with PBKDF2 on first use. A password still at HyperHDR's or the C++ build's
  factory default becomes this version's default (`overglow`, flagged as default).

Converting is a pure function of the source, so importing the same file again changes nothing.

**First start and data directory.** The data directory defaults to the per-user `Overglow` folder
(`%LOCALAPPDATA%\Overglow`, `~/Library/Application Support/Overglow`, `$XDG_CONFIG_HOME/Overglow`), or
`backend/.local/data` for a build that runs from the source tree. When `settings.db` has no first-start marker, the
first existing of `<data>/db/overglow.db` (the C++ build's database in the same folder) and
`<legacy>/db/hyperhdr.db` is imported (`--legacy-data-dir`, default HyperHDR's folder: `~/.hyperhdr` if it exists,
else `%LOCALAPPDATA%\HyperHDR` / `~/Library/Preferences/HyperHDR` / `~/.config/HyperHDR`; source-tree builds don't look
unless told). A failed import is logged and the app starts with defaults; it isn't retried automatically.
`overglow import <file> --data-dir <dir>` imports a database explicitly (the current settings go to `backups/` first).

**Backups.** `save-db` returns the C++ export format (`Overglow_export_format_v1`: `instances` + `settings` rows with
JSON text, per-instance types under instance 0, each controller's `device` under its id); `load-db` also accepts
`HyperHDR_export_format_v17`/`v20`. Credentials are never part of a backup.

## Golden fixtures

`tests/fixtures/<area>/cpp-*.json` are exported from the C++ reference by the Qt tests in `tests/golden/` (ctest label
`golden`): each one runs the real C++ code on deterministic inputs. By default ctest compares their output with the
committed files, so it fails if the reference changes; `OVERGLOW_UPDATE_FIXTURES=1` rewrites them (then commit them):

```powershell
$env:OVERGLOW_UPDATE_FIXTURES = '1'; ctest --preset win-release -L golden; Remove-Item Env:OVERGLOW_UPDATE_FIXTURES
```

Input images are not stored but described by a small spec (`solid`, `hgradient`, `vgradient`, `quadrants`, `checker`,
`random` (xorshift32), `overlay`, `hex`; see `tests/golden/TestImages.h`) that `Golden/TestImages.cs` rebuilds with the
same integer math; each case carries an FNV-1a checksum of the pixels, checked first. Floats are written with the
shortest text that reads back to the same float. `Golden/GoldenDiff` lists every mismatch (where, expected, actual,
difference) up to 20 per failure.

| Folder | C++ source | C# test | |
|---|---|---|---|
| `layouts/cpp-parsing.json` | `LedString` | `MappingGoldenTests` | parsed areas, grid size |
| `mapping/` `{ leds, tolerance, cases: [{ image, width, height, fnv1a, mappingType, sparseProcessing, horizontalBorder, verticalBorder, expected }] }` | `ImageColorAveraging` | `MappingGoldenTests` | owner's 168 LEDs and 3 other layouts, 8 images, 3 sizes (+512x288, 7x5), both mapping types, sparse, borders |
| `color/` `{ step: process \| calibrate, enabled, colorOrder, adjustment, tolerance, inputs, expected, expectedBytes }` | `InfiniteProcessing`, `LedDevice` rounding | `ColorGoldenTests` | owner's settings, 23 variations, 6 color orders, 4 calibration-only; 185 colors each |
| `blackborder/cpp-detector.json` | `BlackBorderDetector` | `BlackBorderGoldenTests` | 15 pictures x 3 sizes x 3 thresholds x 4 modes |
| `hdr/` | `DefaultLut`, `LutLoader`, `decodeBGRA` | `HdrGoldenTests` (Capture.Tests), `ScalerTests` (GPU) | default table (MD5, grid, grays) and the CPU path match exactly; the GPU LUT path matches exactly without scaling and within 1 level of the mean of the looked-up block colors with scaling (the C++ code point-sampled) |
| `smoothing/` | the 6 interpolators (`Infinite*Interpolator`), injected time line | `SmoothingGoldenTests` | owner's settings with each type plus 4 variations (smoothing factor, y_limit, stiffness/damping, 25 Hz); frames at 60 Hz, ticks at the update interval, completion flag and colors match exactly |
| `effects/` | the 10 kept effects | `EffectsGoldenTests` (smoke) | port step 5 |
| `rgbw/` | `RGBW::rgb2rgbw`, `InfiniteColorEngineRgbw` | `RgbwGoldenTests` (Devices.Tests) | every algorithm and the 4 dithering setups match byte for byte |

`settings/owner-hyperhdr-v22.db` is the owner's HyperHDR v22 database, sanitized: admin password, salt and session
token replaced (the password is `fixture-password`), meta uuid replaced, WLED host blanked, microphone and display
names replaced by `default` / `auto`, file vacuumed (no free pages with old content). Everything else (168 LEDs,
color, smoothing, effects, capture, removed-feature records) is as the owner had it.

Tolerance is 0 for every exported fixture: the mapping sums integers and does the same float conversion and divisions
as C++, and the color pipeline builds the same float tables (`powf` of the same C runtime) and runs the same float
operations in the same order (no FMA contraction on either side). Hand-written fixtures (`hand-*.json`) state their own
tolerance. The areas marked smoke only check that the fixtures load and that their inputs can be rebuilt; their
comparison tests are skipped until the port step named in the skip reason.

## Known gaps and next steps

- The primaries calibration (`classic_config`) applies the transposed target matrix, as in C++ (confirmed by
  `color/cpp-calibrate-matrix*.json`): pure red becomes (red.R, green.R, blue.R), while the 3D LUT mode maps pure red to
  the red target. Ported as is; worth checking on the hardware before choosing one behavior.
- `ColorAdjustment.FromJson` gives missing calibration colors their ideal value; the C++ code reads them as black. The
  settings schema always stores all of them, so it only matters for hand-made settings.
>>>

- Effects and music mode (port step 5b): they register with `LightingEngine.Sources` (an `ILightSource` renderer and a
  smoothing configuration from `AddSmoothingConfig`) and add their component to `Components.All`.
- The screen source stays active while `SYSTEMGRABBER` is on; HyperHDR marked it inactive after 800 ms without
  frames, which let a lower priority (a background effect) show. Comes with effects, which need it.
- The Hybrid and HybridRgb interpolators never report a finished transition while a target channel is below the
  minimal backlight (black, or any fully saturated color), so smoothing keeps sending at the update frequency even
  without `continuousOutput` (C++ behavior, confirmed by the fixtures; costs ~0.5 % of one core). Worth fixing once
  the owner has chosen an interpolator.
- Drivers: RGBW dithering uses the device's `refreshTime` rather than the smoothing clock. The Hue API v2 channel
  identify (flashing a channel of a running stream) isn't ported. Serial USB ids are read from the Windows device path
  (Linux/macOS list the ports without them). Only controller 0 runs, as before.
- Screen capture: rotated monitors are captured in scanout orientation (the C++ grabber mis-scaled them); the
  "please wait" image while the default LUT is built is not shown (frames pass without the LUT for ~0.3 s);
  `serverinfo.systemGrabbers.modes` lists only the captured monitor (Engine/Server should list `Monitors`); the
  duplication stays open while `SYSTEMGRABBER` is off; HDR content has no highlight roll-off (clipped at SDR white, as
  in C++). The desktop tests skip when the desktop delivers no frames (monitor off).
- Multiple controllers: the store keeps a list of controllers and `DeviceManager` fans out to a list, but the engine
  and the protocol still use controller 0 (`device`) only.
- The UI's restore dialog still says the app closes to apply a backup; it doesn't any more.
