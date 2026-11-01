# Backbloom backend (.NET)

The C# / .NET 10 backend of Backbloom, ported from the C++ HyperHDR fork it replaces. Behavior was ported file by file
and is compared with golden fixtures exported from the C++ code, which was deleted once this backend reached parity
(it is in the history up to commit `9f49bd2`; doc comments still name the C++ files, e.g. `sources/...`, they refer to
that commit). See [the fixtures README](tests/fixtures/README.md) for the frozen fixtures and the deliberate
deviations from HyperHDR.

Status: **port steps 1-4, 5a, 5b, 6a and 6b** (engine features: smoothing, priorities, black borders, system load; see
[Engine features](#engine-features-port-step-5a); effects and music mode, see
[Effects and music mode](#effects-and-music-mode-port-step-5b)). The React UI in `ui/` connects to it, screen capture
(or a synthetic test pattern) drives the LEDs through the real mapping and color pipeline, and every LED driver of the
C++ build runs on its own device thread (see [LED drivers and discovery](#led-drivers-and-discovery)).
LED layout parsing, image to LED mapping, color processing, black border detection and the ten effects match fixtures
exported from the C++ reference exactly. Settings (port step 4) live in SQLite, cover every kept settings type and are imported from
HyperHDR databases. Windows screen capture with HDR tone mapping (port step 3) drives the pipeline from the
`systemGrabber` settings; see [Screen capture](#screen-capture-windows). Port step 6c makes it a desktop app (tray
icon, a window with the UI, single instance, autostart, sleep and lock handling, log file; see
[Desktop shell](#desktop-shell-port-step-6c)) and adds the [MQTT integration](#mqtt-integration). Port step 6b drives
several LED controllers from one layout (see [LED controllers and the layout](#led-controllers-and-the-layout-port-step-6b)).

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
dotnet run --project src/Backbloom.App -c Release -- --port 18290
```

Tests never listen on the network: test hosts pass `--bind 127.0.0.1`, fake servers and devices bind to loopback,
and UDP sockets aimed at loopback are bound to it. `tests/Shared/NetworkListenerGuard.cs`, linked into every test
project as an assembly fixture, samples the test process's TCP listeners and UDP sockets and fails the assembly when
one is on a non-loopback address (each would make Windows Firewall prompt once per test executable).

Command-line options (the settings ones also as `BACKBLOOM_<NAME>` environment variables). Unknown options and invalid
values are errors (exit code 2, with the usage); `--name value` and `--name=value` both work:

| Option | Default | |
|---|---|---|
| `--port` | `18290` | Web port (HTTP, WebSocket JSON-RPC, UI files). HyperHDR keeps 8090/8092. |
| `--bind` | `127.0.0.1`, plus every interface address while `network.remoteAccess` is on | The one address to listen on; it overrides the remote access setting (advanced use). Without it every copy, installed or not, listens on loopback only until the user turns remote access on (see [First run, HyperHDR and network access](#first-run-hyperhdr-and-network-access)), so nothing makes Windows ask for network access unannounced. |
| `--data-dir` | per-user `Backbloom` folder; `backend/.local/data` when run from the source tree | `settings.db` (settings, controllers, credentials as hashes, instance id), `backups/`, `imported/`, `logs/`, `window.json`, `webview/` (the window's browser profile). See [Settings](#settings-storage-and-hyperhdr-import). |
| `--legacy-data-dir` | HyperHDR's data folder; none when run from the source tree | Where the first start looks for `db/hyperhdr.db` to import. `none` turns it off. |
| `--ui-dir` | `ui/` next to the program, else `ui/dist` of the source tree | Built React UI to serve. |
| `--capture` | `screen` | Frame source: `screen` (DXGI desktop duplication, configured by `systemGrabber`; falls back to the test pattern on other OSes) or `synthetic` (test pattern, used by the server tests). |
| `--capture-image` | none | A still picture (binary PPM, P6) as the frame source instead (it overrides `--capture`); scaled down to at most 512 px wide. For screenshots and demos with known colors. |
| `--fps` | `60` | Rate of the synthetic test source or the still picture. |
| `--lut-dirs` | data dir, app dir | Directories searched for `lut_lin_tables.3d` (or `.3d.zst`), separated by `;`; the default table is written to the first. |
| `--no-window` | | Start in the tray without opening the window (the autostart entry uses it). |
| `--service` | | No tray icon, no window, console log: headless use (C++ `--service`). Power events are still handled. |
| `--update-url` | placeholder (updates off) | Release feed of an installed copy (see [packaging/README.md](../packaging/README.md)). |
| `--update-channel` | the installed package's | Release channel to follow (`stable`, `beta`, ...). |
| `--import <file>` | | Import a HyperHDR database into the data directory and exit. |
| `--quit` | | Ask the instance running with the data directory to quit; waits until it is gone (installers, scripts). |
| `--version`, `-h`/`--help` | | Print and exit. |

`backbloom.exe` is a GUI program (no console window when started from Explorer or autostart); started from a terminal it
attaches to that console, so the usage, `--version` and the log of `--service` show there. Ctrl+C stops it gracefully.

### Using the UI

- Served by the backend: `cd ui && npm ci && npm run build`, then build the backend (the UI is copied next to the
  program as `ui/`) and open `http://127.0.0.1:18290`, or use the app's window.
- From a dev or preview server: `npx vite preview --port 4730` in `ui/` and open
  `http://localhost:4730/?backend=localhost:18290`.

The UI finds its backend at the page's own host (`ui/src/api/backend.ts`: `?backend=` / `VITE_BACKEND` first), so it
needs no change when the app serves it. `UiFiles` serves `index.html` for `/` and for any other GET without a file
extension (single-page fallback; the UI routes by URL hash today), caches the content-hashed `assets/` for a year
(`immutable`) and revalidates everything else (`no-cache` with ETag), so a new version shows up at once. Missing files
with an extension stay 404. After rebuilding the UI, rebuild the backend (or pass `--ui-dir ../ui/dist`) to serve it.

The UI from this computer (the app's window, a browser here) never asks for a password; other devices sign in (see
[Access](#access-this-computer-and-other-devices)).

### Access: this computer and other devices

`Backbloom.Server/Auth/LocalAccess` decides, per connection:

| Peer | Rights |
|---|---|
| loopback: 127.0.0.0/8, `::1`, IPv4-mapped `::ffff:127.x` | full access (API and configuration), no password |
| anything else (only reachable with remote access on, or `--bind`) | none until it signs in with the password, a user token or an API token; refused (403) while no password is set |
| MQTT `JsonAPI` | API rights, no configuration access (anyone who can publish on the broker could send it) |

- Only the socket's peer address counts (Kestrel's `RemoteIpAddress`; no forwarded-headers middleware), so
  `X-Forwarded-For` / `Forwarded` can't make a client local, and a browser here that opens this computer's LAN address
  is another device.
- DNS rebinding and cross-site requests: any website open in a local browser can send requests to 127.0.0.1, so
  `LocalRequestGuard` (the first middleware) refuses loopback requests whose `Host` isn't `localhost`, a 127.x
  address or `[::1]` (any port), or whose `Origin`, when there is one, isn't such a host over http(s) (`null` origins
  included). It applies to the UI files, `POST /json-rpc` and the WebSocket handshake alike. Other devices may use
  any host name (`<pc>.local`, its address); they have to sign in anyway.
- No factory password. `AuthService` starts without one; `authorize newPassword` sets it without the current password
  from this computer (also how a forgotten one is replaced) and for the first password, while a device that signed in
  must give the current one. A HyperHDR password other than its factory default is imported and counts as set.
- Remote access needs a password: `setconfig` refuses `network.remoteAccess: true` without one
  (`ConfigCommand.PasswordNeeded`), and `RemoteAccess` keeps the network endpoints closed until one exists (a
  restored backup or an older database can't open them either). The UI's remote access dialog asks for the password
  (twice, with a strength hint) before it turns remote access on.
- HyperHDR's `apiAuth`, `localApiAuth`, `localAdminAuth`, `internetAccessAPI`, `restirctedInternetAccessAPI` and
  `ipWhitelist` are gone from the `network` schema: imports drop them (`LegacyMigrations.RemoveAccessOptions`)
  and `setconfig` ignores them, so HyperHDR clients that send the whole section back keep working.

## Projects

```
src/
  Backbloom.Core       pure logic: LED layout, image->LED mapping, color processing, black borders, smoothing, priorities,
                      the effects and the music analysis (FFT, bands) (no I/O, no threads)
  Backbloom.Capture    frame source abstraction, frame pacer, synthetic test source, capture settings, CPU scaling
                      path, HDR tone mapping table (LUT loader, default LUT)
  Backbloom.Capture.Windows  DXGI desktop duplication + Direct3D 11 scaling / HDR shader (TerraFX interop)
  Backbloom.Audio      sound capture abstraction, soundEffect settings, device sessions, conversion to analysis blocks
  Backbloom.Audio.Windows  WASAPI loopback and recording capture, endpoint enumeration (TerraFX interop)
  Backbloom.Devices    LED device lifecycle (LedDeviceRunner), RGBW encoders and the 16 drivers (network, serial, HID,
                      file); driver tools for leddevice discover/getProperties/identify
  Backbloom.Discovery  mDNS client/responder, SSDP search, UDP broadcast probes, serial port and HID enumeration
  Backbloom.Mqtt       the one MQTT broker connection (MQTTnet), shared by Zigbee2MQTT and the MQTT integration
  Backbloom.Settings   settings schemas + validation, SQLite store, credential store, HyperHDR import, backups
  Backbloom.Engine     orchestration: frame loop, component switches, controllers, live preview feed, running effects,
                      the lighting gate (first run, HyperHDR running)
  Backbloom.Server     Kestrel: JSON-RPC over WebSocket + POST /json-rpc, auth, UI files; the MQTT integration
  Backbloom.Shell      desktop integration, platform-neutral: power/session policy, tray menu, window placement,
                      single instance, the contracts of tray, window, autostart and system events
  Backbloom.Platform.Windows  Windows implementations of the platform interfaces: high-resolution clock, machine CPU and
                      memory load, power and session events, tray icon, WebView2 window, Run-key autostart
  Backbloom.App        host: composition root (DI), command line, single instance, logging, lifetime (exe: backbloom)
tests/
  Backbloom.*.Tests    xUnit v3; fixtures/ holds golden fixtures shared by the test projects
```

Dependency rules (a project may only use the ones above it in this list):

1. `Core`, `Settings`, `Discovery`, `Mqtt` (no project references)
2. `Capture` (Core), `Audio` (Core), `Devices` (Core, Discovery, Mqtt); `Capture.Windows` (Capture), `Audio.Windows` (Audio)
3. `Engine` (Core, Audio, Capture, Devices, Settings)
4. `Server`, `Shell` (Engine and below); `Platform.Windows` (Engine, Shell)
5. `App` (everything; the only place that creates services)

`Backbloom.Engine` is an addition to the plan's layout: the API layer needs one orchestrator to talk to
(component switches, live colors, identify), and it can't live in `App` without the server depending on the host.

Platform code lives only in `*.Windows` / `*.MacOS` / `*.Linux` projects behind interfaces defined in the projects
above (`Capture.Windows` implements `IScreenCaptureSource`, `Audio.Windows` implements `IAudioCapture`; `App` creates
them only on Windows).

The effects live in `Core` (as the plan says): they and the music analysis they read are pure logic, verified against
the C++ fixtures by `Core.Tests`; `Audio` only gets the sound into that analysis, `Engine` runs effects as sources.

## Threading model

| Thread | Owner | Does |
|---|---|---|
| Frame loop (1, above-normal priority) | `LightingPipeline` | capture (or step the visible effect) -> map -> slow down the "Screen average" LEDs -> process -> zones (step their effects without waiting, write their colors) -> hand frames to controllers and the preview feed |
| Sound capture (1 while a music effect runs, above-normal) | `WasapiSession` | read the device's buffers, convert them into blocks, analyze each block |
| Smoothing (1, above-normal priority) | `OutputStage` | while smoothing runs: one tick per update interval (interpolate, anti-flicker filter, hand the frame to the controllers and the preview); idle otherwise |
| One per controller | `LedDeviceRunner` | open (with retries), send the newest frame (its slice of the LEDs), refresh, blink, close |
| Thread pool | ASP.NET Core, `OtherAppMonitor`, `ProfileMonitor` (and `ModeMonitor` after it), `PerformanceRecorder` | JSON-RPC requests (in order per connection), event pushes, LED stream timers, the HyperHDR process check, (while the layout has profiles) the profile programs check every 2 s, and the performance samples (CPU every 2 s, a sample every 10 s) |
| UI (1, STA, Windows) | `UiThread` | Win32 message loop: hidden message window (power/session notifications, tray callbacks), tray menu, WebView2 window |
| Log file (1, below normal) | `RollingFileLoggerProvider` | writes queued log lines |

- The frame loop never waits on a controller or a client. `LedDeviceRunner.Submit` copies the colors into a
  preallocated buffer; if the device thread hasn't sent the previous frame yet, that frame is dropped (counted in
  the stats), never queued.
- Settings changes arrive on request threads. They build new immutable objects (`PipelineConfig`: LED map options +
  `ColorProcessor`) and swap them in with one reference write; the frame loop picks them up on its next frame.
- When `layout`, `device` or `leds` change, a controller is recreated only if its driver settings, name or LED count
  changed (the old one is switched off first: black frame, power off); the others keep running at their new position.
- Events (`*-update`) are raised on the thread that caused the change; each WebSocket session serializes them into
  its own outbound queue, drained by one send loop.

## Hot-path rules

The frame loop, `LedDeviceRunner.Submit` and the sound capture loop must not allocate in steady state
(`GC.GetAllocatedBytesForCurrentThread` tests enforce it in Core, Devices, Engine, Capture, Capture.Windows and Audio;
Core's measure on a dedicated thread after a full collection, in a collection that runs without parallel tests, since
the counter of a test-runner thread occasionally grew by 4-6 KB under full-suite load without any allocation by the
measured code):

- buffers are sized when the LED count or frame size changes, not per frame;
- `ImageToLedMap` precomputes pixel offsets per LED into flat arrays;
- `ColorProcessor` precomputes its lookup tables and is immutable;
- `BlackBorderDetector` samples a few lines of the frame in place; `BlackBorderProcessor` only keeps counters;
- zones keep their LED indices, linear colors and a scratch buffer for the color pass; their effects step and map like
  the visible one (`ZoneTests` measures the screen path with and without smoothing and the idle path);
- effects step on preallocated state (colors rotate in place, the canvas and its angle table are kept); the music
  analysis and `AudioBlockWriter` work in fixed buffers, and the WASAPI loop hands the device's buffer to the writer as
  a span (`WasapiSession` measures its own allocations; a live loopback test asserts 0);
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
| Monitors | `MonitorEnumerator`: DXGI outputs plus the display configuration API. Setting values: `auto` (primary monitor; HyperHDR took the first sorted entry, which could be a multi-monitor one), `\\.\DISPLAY5\|NVIDIA GeForce RTX 5080` (HyperHDR's id, kept so imported settings work and the UI can show it), the stable monitor device path (or `edid:manufacturer:product:connector` when the driver reports none), or `MULTI-MONITOR\|adapter` (all monitors of an adapter side by side, ordered by `reorder_displays` as `std::next_permutation` did). Windows renumbers the GDI names after a driver update or a reconnect (`DISPLAY5` came back as `DISPLAY9`), so a single-monitor setting (`MonitorEnumerator.SelectFor`, a fallback logged once as a warning) captures the monitor with the device path it matched last in this run (also when another monitor took the old name); one that matched nothing yet in this run captures the only connected monitor, if there is just one (not remembered, so a monitor that is away, like a TV switched off, is waited for and not replaced by the remaining screen). Still open (roadmap): after a restart with several monitors a stale id matches nothing (or, if another monitor took the old name, that one), and the UI only offers Auto and the captured monitor. |
| Duplication | `IDXGIOutput6::DuplicateOutput1` (the capture thread is made per-monitor DPI aware, otherwise it fails with `DXGI_ERROR_UNSUPPORTED`). HDR is detected per monitor (`DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020`); with hardware on and an SDR white level above 0 (see SDR white below) the desktop is duplicated as FP16 scRGB (10-bit PQ as a fallback), else as 8-bit BGRA, which Windows converts to SDR. |
| GPU path (`hardware`) | `GpuScaler`: the acquired texture is copied into an own texture (the frame is released at once), `Shaders/Downscale.hlsl` averages each `divide` x `divide` block of the cropped area (bilinear taps between texels, up to 8 x 8 per pixel) into a small BGRA target, then one staging readback of that size. Modes: scRGB scaled by 80 / SDR white level + BT.709 OETF (the C++ HDR shader, reconstructed from its bytecode), PQ/BT.2020 -> BT.709, the LUT as a 256^3 RGBA8 3D texture read with exact texel loads (8-bit captures that tone mapping applies to, see below), or plain SDR. The shader only runs when the image or the mode changed. |
| CPU path | `CpuScaler` + `BgraDownscaler` (port of `decodeBGRA`): full-size 8-bit readback, every `divide`-th pixel, LUT on the CPU. Used when `hardware` is off or the shaders can't be created. |
| Scaling, crop | `CaptureRegion`: the smallest whole factor that makes the cropped width fit `videoMode` (as `getTargetSystemFrameDimension`). Crops are in screen pixels on both paths (the C++ GPU path cropped the scaled image) and are ignored when they leave 16 pixels or less. |
| Pacing, latency | `fps` via `FramePacer`; then `AcquireNextFrame(0)` returns the newest desktop image (no new image: the last one is delivered again from the staging texture; only the pointer moved: no GPU work). Until the first image after (re)start it waits up to 100 ms. |
| Recovery | `DXGI_ERROR_ACCESS_LOST` / invalid call / device removed (display mode or HDR change, desktop switch, driver reset): everything is released and rebuilt 100 ms later. `E_ACCESSDENIED` (secure desktop: UAC, lock screen): retried every second; the thread attaches to the input desktop (`OpenInputDesktop` + `SetThreadDesktop`) so a service can follow it. Missing monitor: retried every 5 s. A retry waits instead of spinning; the same problem is logged once. |
| Tone mapping | `hdrToneMapping` (`ToneMapping.AppliesLut`): `auto` (default) applies the LUT to the 8-bit capture of a monitor only while Windows drives it in HDR mode, `on` to every 8-bit capture (HDR10 content shown without Windows HDR), `off` never. An HDR capture (FP16/PQ) is always converted by the shader, never by the LUT. The decision is made when a monitor's duplication opens; toggling Windows HDR ends the duplication, so it follows within ~100 ms. HyperHDR's switch applied the LUT to SDR monitors too, which oversaturates normal content (red/magenta shift); imports and older databases turn on into `auto` and off into `off`. `serverinfo.systemGrabbers.hdr` = `{ mode, capturing, monitorHdr, toneMapping, sdrWhiteNits }` (tone mapped by the shader or the LUT; the SDR white level of an HDR capture, 0 without one), pushed as `capture-update` (the whole `systemGrabbers` object) when it changes. |
| SDR white | The level an HDR desktop is converted with (`CaptureSettings.SdrWhiteNits`). With `monitor_nits_auto` (default, a Backbloom addition) it is Windows' "SDR content brightness" of the monitor: `MonitorEnumerator` reads `DisplayConfigGetDeviceInfo(DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL)` (`SDRWhiteLevel`, 1000 = 80 nits) with the monitor names when the capture starts, and the capture loop asks again every 2 s (one non-allocating call, since a brightness change alone needn't end the duplication) and reopens when it changed. Without a reported level, and with the switch off, `monitor_nits` applies (0 turns the HDR capture off). |
| LUT | `LutLoader`: first existing `lut_lin_tables.3d` or `.3d.zst` wins; one table or the legacy three-table file (first table used). `LutProvider` loads in the background on first use (only when tone mapping applies to an 8-bit capture) and otherwise builds the default table (`DefaultLut`, bit-identical to C++, ~0.3 s on 24 cores) and saves it to the first LUT directory. |
| Settings | `systemGrabber` (schema in `Backbloom.Settings`); `App` reconfigures the source on every change of that section, the next frame restarts the capture. |

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

Port of `InfiniteSmoothing` and the six interpolators (`Backbloom.Core/Smoothing`), bit-identical to the C++ ones
(`fixtures/smoothing`, tolerance 0), except that the Hybrid and HybridRgb transitions now finish when a channel's
target is below the minimal backlight (HyperHDR kept them running, and sending, forever) and that their spring is
integrated in steps the spring allows, at most 20 ms (one step per tick diverged for stiff springs or long ticks; see the fixtures
README). C++'s Stepper and Rgb interpolators differ only in target blending, so
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

Measured (Release, then `SmoothingProfile`, explicit test): owner's 168 LEDs, synthetic source at 60 fps, four file
controllers to the null device: 1.25 % of one core without smoothing, 1.72 % with HybridRgb at 50 Hz (the test is now
`PipelineProfile`, see [LED controllers and the layout](#led-controllers-and-the-layout-port-step-6b)). Neither path
allocates (`Pipeline_SteadyStateDoesNotAllocate`, `SmoothingTicks_DoNotAllocate`).

### Sources and priorities

Port of `Muxer` (`Backbloom.Core/Priorities`) behind `PriorityManager` (Engine: lock, 250 ms timeout timer, events). The
lowest priority number among the active sources is visible, unless one was selected manually (`sourceselect`); 255 is
"nothing" (black, and the controllers are switched off, as HyperHDR did; the `LEDDEVICE` switch keeps the user's
choice). The screen registers at `systemControl.systemInstancePriority` (245) while `ALL` and `SYSTEMGRABBER` are on.
`color` registers a static color (priority 1..253, optional `duration` ms, origin `<origin>@<peer>`); static colors
skip the color settings as in HyperHDR, are drawn once per change and then smoothed. `clear` takes -1 (all colors and
effects) or 1..253, `clearall` = -1. `serverinfo.priorities` lists the visible source first with `priority`,
`componentId`, `origin`, `owner`, `active`, `visible`, `duration_ms` and, for colors, `value: { RGB, HSL }`;
`priorities_autoselect` and `activeLedColor` as in HyperHDR. Effects implement `ILightSource` (render linear LED colors
on the frame loop while visible) and register with `Sources.Register` and `AddSmoothingConfig`.

**Screen activity** (`ScreenActivity`, HyperHDR's `SystemControl` rule): the screen registers inactive, becomes active
with its first frame and inactive after 800 ms without one (lock screen, monitor off, capture errors), so the source
below it shows: the background effect, or nothing. With nothing left the controllers are switched off, but while capture
is on only after `LightingEngine.NoSourceOffDelay` (10 s; HyperHDR: at once): until then they stay connected and show
black, so a display change that stops the capture for a second doesn't switch them off and reconnect them (WLED once
failed to reconnect for minutes after that). Switching capture or the LEDs off still switches them off at once. The
frame loop only captures for a visible
screen, so while the screen is inactive it probes the frame source every 500 ms (20 ms budget while an effect shows, so
the effect keeps its pace; up to 250 ms while idle); the first frame makes it active again. A static desktop still
delivers frames (DXGI repeats the last image), so it never counts as inactive. While a source above the screen shows
(the startup effect), an active screen isn't watched.

### Black borders

`BlackBorderProcessor` runs on screen frames while the `BLACKBORDER` component is on (initially
`blackborderdetector.enable`; settings changes apply at once); the detected border is left out of the LED mapping (the
map is rebuilt only when the border changes). Off: no border. Unlike HyperHDR the border survives a frame size change.

### System load

`performance-update` reports the whole computer's CPU and memory (the UI's "whole computer" tiles), from
`ISystemMetrics`: `WindowsSystemMetrics` (`GetSystemTimes`, `GlobalMemoryStatusEx`; HyperHDR used the PDH processor
counter, the same quantity), else this process's share (`ProcessMetrics`). LED updates/s is what the controllers
actually sent (the smoothing rate while smoothing runs).

### Performance recording

`PerformanceRecorder` (Engine) records how the app performs, for looking into a problem afterwards (a gaming session,
a user's crash). When the app starts it writes a start line; then every 10 s one sample, a JSON object on one line,
from counters that already exist (the engine's frame count, the controllers' `FrameStats`, `Environment.CpuUsage`,
`GC`, `Process`, its own `ISystemMetrics` instance, keyed `recorder`, so the UI's tiles keep their own interval). A
timer ticks every 2 s for the CPU peak; the sample is taken on the tick closest to 10 s (ticks come a few ms early or
late). It runs on a timer thread, never on the frame loop; a sample costs a few ms of CPU (one `Process` snapshot), and
an exception only skips the sample (logged once). `PerformanceRecording` (App) appends the lines to
`logs/performance-yyyyMMdd.jsonl` (opened and closed per line, so the file can be read while the app runs; a new file
per day, the newest 14 files kept, i.e. the last 14 days the app ran; about 4 MB per full day plus 1.3 MB per
controller).

The start line and the last 30 minutes (180 samples) stay in memory as their JSON lines. When the app ends on an
unexpected error, `CrashReport` first appends the `FATAL` entry, naming `crash-yyyyMMdd-HHmmss.jsonl`, then writes the
lines there (the newest 10 kept; a failure there never costs the entry), so a crash report can send the error, the log
and the performance before it together. The recorder is taken once the app runs, so the crash path resolves no
services. Up to 10 s before the crash are missing (the last sample is the last complete one).

The log gets a warning when a controller starts dropping frames (at least 10 and 5 % of the frames handed to it in one
sample; a frame counts as dropped only while the controller is ready: while it opens or waits to retry nothing is
taken, which isn't falling behind) and a line when it
keeps up again (below 1 %), and a warning when garbage collections paused the app for
100 ms or more in one sample.

GPU: `IGpuMetrics` (`WindowsGpuMetrics`, Platform.Windows) reads the performance counters Task Manager uses (PDH:
`GPU Engine(*)\Utilization Percentage`, `GPU Adapter Memory(*)\Dedicated Usage`, `GPU Process Memory(*)\Dedicated
Usage` / `Shared Usage`) on every 2 s tick: the busiest engine of any adapter, for the computer (summed over
processes) and for Backbloom (the screen capture), time-weighted over the sample plus the peak tick, and the GPU
memory in use. The query opens on the first tick (about 140 ms, not on the app's start); a reading then takes about
1 ms. The wildcards are expanded on every collection, so the capture's device, created later, is included. With
hundreds of instances a reading parses the names in place from one reused buffer and keeps a total per engine between
readings, so it hardly allocates (the app's allocation numbers describe Backbloom, not its measuring). A failure only
leaves the GPU unknown.

LED power, for overheating questions: `LedDeviceRunner` measures each frame it sends on the device thread (not the
frame loop; one pass over the colors, no allocation): the means of the red, green and blue channels (the share of full
brightness the LEDs' PWM draws; the load is their mean), the brightest single LED, and how long the driver took to
send it. `FrameStats` sums them and keeps the peaks, which only the recorder takes (through the engine) once per
sample. The estimated current uses the controller's own LED current when it reports one (WLED's "LED current" per
output, 255 meaning WS2815 at 12 mA), else WLED's default of 55 mA per LED at full white (WS281x at 5 V); it is what
Backbloom asks for, before the controller's own brightness and power limit, and it is left out for lamps (Hue, LIFX,
Nanoleaf, WiZ, Yeelight, Home Assistant, Zigbee2MQTT) and the file driver.

Drivers whose controller reports about itself implement `ITelemetryDriver`. The WLED driver (checked against WLED's
json.cpp, cfg.cpp and bus_manager.cpp, v16) reads `GET /json/cfg` once per connection for the power limiter: the global
limit `hw.led.maxpwr`, each output's limit `hw.led.ins[].maxpwr` and LED current `ins[].ledma`; as WLED's
`initializeABL`, the limiter is in use with a global limit and an output with an LED current, or without a global one,
with an output that has both (then the limit is their sum); WLED 0.14 and older have one LED current `hw.led.ledma`
and treat a limit below 150 mA as none; and "force max brightness" for realtime data
(`if.live.maxbri`). While connected it reads `GET /json/si` (state and info, without the effect lists) every 10 s
(never in `Write`, never while switched off; the readings are published only once a connection succeeded and cleared
on close, which waits for the poll to end, so nothing it sends can come after the switch-off). It records WLED's
current estimate only while the limiter is in use (`leds.pwr` is then the LEDs plus the controller's own 80-120 mA; v16
reports those 80-120 mA without the limiter, and `leds.maxpwr` is the global setting whether in use or not), its
brightness (`state.bri`: applied on top of what Backbloom sends unless "force max brightness" is on; a change
between readings is logged, e.g. made in the WLED app), on, frame rate (`leds.fps`: LED updates, one per UDP packet
above 489 LEDs), LED count, realtime state and sender (`live`, `lm`, `lip`: shows another program streaming to it),
realtime override (`lor`: 1 until the realtime data ends, 2 until a restart), connected WLED apps (`ws`), chip, Wi-Fi,
free memory, uptime and a temperature: entries of `sensor` and `u` with "temp" in the name and a degree unit (the
Temperature usermod reports a missing sensor as `[0, " Sensor Error!"]`, which is skipped; "internal" names the chip's
own, and a sensor elsewhere is preferred). A restart is noticed when the boot time (reading time minus uptime) moves by
more than 30 s: while connected it is counted, logged as a warning (power or heat) and the realtime state (on,
brightness) is sent again, as WLED forgot it, on every reading until that succeeds (a controller that just booted may
not answer yet); noticed on connecting, it happened while switched off (e.g. the power
supply was off) and is only logged. Three failed reads in a row are logged.

The log warns when the limiter holds the LEDs at 95 % of its limit or more (until below 80 %) and when a temperature
reaches 65 °C at a sensor or 85 °C for the chip (ESP32 chips run at 50-70 °C; WLED's own alarm is at 95 °C), until 5 °C
below.

For the crash report upload (not built yet): controller names and ids and the profile id are text the user typed;
decide what is sent before uploading the lines. Short stutters don't show in 10 s averages; a frame-interval maximum
or a count of late frames would be the next fields to add.

Format, version 1 (fields are only added; a change of meaning raises `v`). Every line has `v` and `kind`.

The start line (`kind` "start"):

| Field | |
|---|---|
| `time` | local time with offset |
| `version` | app version and build (`0.1.0+1a2b3c4d`) |
| `os`, `runtime`, `cores` | operating system, .NET runtime, logical processors (`app.cpu` is a share of all of them) |
| `pid`, `processStart` | process id and start time: a new start line marks a restart |

A sample (`kind` "sample"):

| Field | |
|---|---|
| `time`, `seconds` | local time with offset at the end of the sample, its length |
| `app.cpu`, `app.cpuPeak` | this process's CPU, share of all cores (as Task Manager) over the sample and the highest 2 s value |
| `app.privateMb`, `app.workingSetMb`, `app.heapMb` | private bytes, working set, managed heap |
| `app.allocatedMbPerSecond`, `app.gcCollections`, `app.gcPauseMs` | allocation rate, collections of generations 0, 1, 2 and the time they paused the app, in the sample |
| `app.threads`, `app.handles` | at the end of the sample |
| `app.gpu`, `app.gpuPeak`, `app.gpuMemoryMb`, `app.gpuSharedMemoryMb` | Backbloom's GPU use (busiest engine, %, over the sample and the highest 2 s value), dedicated and shared GPU memory |
| `computer.cpu`, `computer.memoryUsedMb`, `computer.memoryTotalMb` | the whole computer (null when unknown) |
| `computer.gpu`, `computer.gpuPeak`, `computer.gpuMemoryUsedMb` | the whole computer's GPU use (as Task Manager) and dedicated GPU memory in use |
| `lighting.source`, `lighting.profile`, `lighting.framesPerSecond`, `lighting.smoothingPerSecond` | frame source, active profile id (null: none), frame loop and smoothing rates |
| `controllers[]` | `id`, `name`, `type`, `state` (`Disabled` when nothing is sent), `leds`, `updatesPerSecond` (frames sent), `submitted`, `dropped`, `writeErrors` (in the sample), `latencyMs` (average submit-to-sent time, null without new frames) |
| `controllers[].writeMs`, `writeMsPeak` | time the driver took to send a frame, average and highest |
| `controllers[].load`, `loadPeak`, `brightestLedPeak`, `loadRgb` | average and highest load of the frames sent, % of full white; the brightest single LED's peak; average red, green and blue (null when none were sent) |
| `controllers[].milliampsPerLed`, `estimatedMa`, `estimatedPeakMa` | the LED current assumed and the current the load means (null for lamps and the file driver) |
| `controllers[].controller` | what the controller reports (null when its driver doesn't read that or it is switched off): `ageSeconds` (since it was read), `powerMa` (null: unknown), `powerLimitMa` (null: no limiter), `milliampsPerLed`, `brightness` (0..255), `forceMaxBrightness`, `on`, `framesPerSecond`, `leds`, `live`, `liveMode`, `liveSource`, `realtimeOverride`, `clients`, `signal` (%), `rssi` (dBm), `wifiChannel`, `freeMemoryKb`, `uptimeSeconds`, `restarts` (noticed while this driver ran: across reconnects, reset when the controller's settings change), `temperatureC`, `temperatureSource`, `temperatureIsChip`, `chip`, `firmware`; each null when not reported |

## Effects and music mode (port step 5b)

### Effects

The ten effects the C++ build kept (sources/effects), in `Backbloom.Core/Effects`, match `fixtures/effects` byte for
byte (LED colors, pictures, step times):

| Effect | Class (C++) | Output | Step | Smoothing |
|---|---|---|---|---|
| Rainbow swirl / Rainbow swirl fast | `SwirlEffect` (`Animation_Swirl`) | 80x45 conical rainbow, 1 degree per step | 56 / 19 ms (20 / 7 s per turn) | 200 ms at 25 Hz / effect default |
| Full color mood blobs / Warm mood blobs | `MoodBlobsEffect` (`Animation_MoodBlobs`) | LED colors, 5 rotating blobs | 100 ms | effect default / 200 ms at 25 Hz |
| Breath | `FadeEffect` (`Animation_Fade`) | one color, 50 -> 255 gray and back | 29 ms in, 19 ms out, 12 ms holds | effect default |
| Candle | `CandleEffect` (`Animation_CandleLight`) | LED colors, random flicker | asks 200, runs 100 ms | 500 ms at 20 Hz |
| Music: fullscreen pulse / stereo / quatro / pulse waves (MULTI COLOR) | `Music*Effect` (`Animation4Music_*`) | 80x45 picture from the sound | polled every 17 ms, draws per sound block | direct (no smoothing) |

Five music effects are new in Backbloom (no C++ counterpart, so no fixtures; `EffectTests` covers them). They derive
from `MusicAnimation`, which takes each sound result in `Update` and paints on every 17 ms step in `Paint`, so ripples,
scrolling and falling levels move smoothly although results come 21.5 times a second. Most map the picture to the
frame's edge (`EdgePositions`: the way from the middle of the bottom edge up either side to the middle of the top, each
pixel taking its nearest edge point, so LED areas of any depth see the same colors):

| Effect | Class | Picture | Smoothing |
|---|---|---|---|
| Music: spectrum | `MusicSpectrumEffect` | the eight band levels along the edge, bass at the bottom (red) to treble at the top (violet); levels jump up and fall back slowly | direct |
| Music: VU meter | `MusicVuMeterEffect` | the loudness (curved, since it is relative to the recent maximum) fills the edge from the bottom, green to red, with a white peak held 0.7 s, then falling | direct |
| Music: beat ripple | `MusicBeatRippleEffect` | each beat starts a ring in the music's color (saturated) that spreads from the middle over the edges in 0.85 s; a dim glow between beats | direct |
| Music: waterfall | `MusicWaterfallEffect` | the music's color (saturated, as bright as the music is loud) enters at the bottom and takes 1.6 s up both sides | direct |
| Music: bass glow | `MusicBassGlowEffect` | the whole frame in a color going once around the hue circle in two minutes, brighter with the bass | direct |

Every effect has a one-sentence `Description` for the UI and a `DisplayName` for menus ("Music: stereo for LED strip
(MULTI COLOR)" is shown as "Stereo"; names stay the ids stored in settings).

**Previews** (`EffectPreview`, `effects previews` command). For the effect cards the UI gets a few looping seconds of an
effect on the user's own LEDs (the `leds` list of every controller, so strips anywhere, like one behind the desk and one
on the ceiling, show what they would do), 15 frames a second: 6 s for effects, 4 s for music effects. They are rendered
on simulated time, not on the lights: the effect steps at the times it asks for, each LED takes the mean of its area of
the picture (or its own color from effects that set LED colors; disabled LEDs stay dark), effects with their own
smoothing are followed roughly, and music effects hear `SyntheticMusic` (a two-bar 120 BPM loop: kick, hi-hat, bass,
chords and an arpeggio, bass-heavy in the first bar and brighter in the second), one loop to learn the levels and one
recorded, so the preview loops seamlessly. The UI asks for each effect as its card comes into view; previews are kept
until the layout changes. Without enabled LEDs they use `EffectPreview.StandardFrame` (16 LEDs along the top and
bottom, 9 per side). The UI draws them, and the live preview, on the layout as the Layout page does (each LED where it is
in the room, the walls and ceiling folded out around the front wall).

**Music effects need sound capture.** Without it they would freeze on their last picture (HyperHDR ran them anyway).
`EffectEngine.Run` refuses one while `SoundCapture.CanCapture` is false (`EffectStart.NoSound`; the `effect` command
answers "Music needs sound input, which is off.", the tray greys the music entries out), switching capture off ends
the running ones (what is below shows again), and a music background effect ends and comes back with the capture.
`serverinfo.sound.available` tells the UI, which turns sound input on (`soundEffect.enable`) when a music effect is picked while it is off.

"Effect default" is the 200 ms / 25 Hz configuration the C++ engine registered for effects; equal configurations are
shared (C++ `addCustomSmoothingConfig`), so the engine registers three: default, Candle, direct. Smoothing applies only
while the `SMOOTHING` component is on. Pictures go through an image-to-LED map of their own (80x45, no black border
detection, as HyperHDR disabled it for effects); LED colors through the sRGB table, then the color settings apply as for
the screen.

**Random numbers.** "Candle" and the start hue of "Full color mood blobs" called C `rand()`. `MsvcRandom` is the
Microsoft CRT generator (state = state * 214013 + 2531011 mod 2^32, result bits 16..30, `RAND_MAX` 32767), so the
golden test seeds it with 42 like the C++ fixture export and gets the same sequence. It sits behind `IEffectRandom`; the
engine seeds each effect from `Random.Shared` (HyperHDR never seeded it, and the CRT state is per thread, so every
"Candle" flickered the same way). The random start hue of "Full color mood blobs" has no visible effect in C++ (it adds
whole degrees where only the fraction counts); ported as is.

**Running effects** (`EffectEngine`, `EffectSource`; port of `EffectEngine` / `Effect::run`). An effect is a source at
its priority (`componentId` EFFECT, `owner` the effect name), stepped on the frame loop at absolute deadlines of the
engine clock (its own step time, at most 100 ms as in C++); it pauses while not visible. It ends when its source is
cleared, replaced (another effect or a color at its priority) or its duration is over (the muxer's timeout); a music
effect holds the sound capture until then. A waiting effect returns at once when another source becomes visible.

| Setting | Behavior (as in C++) |
|---|---|
| `foregroundEffect` (startup) | at start, priority 0, `duration_ms` (0 or -1: 3000 ms, logged), effect or color (default "Breath", a short white fade; HyperHDR's was "Rainbow swirl fast"); when the LEDs start disabled, it runs if they are switched on within 5 s; later changes of the setting don't run it |
| `backgroundEffect` | the idle effect: priority 254, until cleared (default "Warm mood blobs" when turned on); every change of the setting replaces it; `clear -1` keeps it; not listed in `activeEffects` |

The owner's configuration (startup "Rainbow swirl fast" 3000 ms, background off) runs the swirl for 3 s at start, then
the screen shows (it became active during the effect, so the controllers stay on).

### Music mode

`MusicAnalysis` (Core) is `SoundCapture::analyzeSpectrum` + `SoundCaptureResult`: blocks of 1024 mono 16-bit samples at
22,050 Hz, the same fixed-point FFT, eight bands, the loudness and the moving color with its integer arithmetic, and the
"two interpolated steps between blocks" smoothing (`enable_smoothing`). The music fixtures feed their recorded audio
blocks through it. Additions for the new effects, which leave the C++ results unchanged: `MusicStats.Band(i)` (the eight
band levels 0..255, each relative to its band's recent maximum) and `MusicStats.Beats`, a beat counter: a beat is a
block whose bass (0-258 Hz) is 1.4 times its average over about the last 8 blocks, at most one per 5 blocks (230 ms).

`Backbloom.Audio`: `SoundCapture` applies `soundEffect` (`enable`, `device`, `enable_smoothing`; changes apply at once,
HyperHDR only read them at the next start) and runs the platform capture while at least one music effect holds it
(C++ `open`/`close` handles). `AudioBlockWriter` turns any device format (float or 16/24/32-bit PCM, any rate and
channel count) into the analysis blocks: channels averaged, each 22,050 Hz sample the mean of the input over its time
span. Without sound capture a music effect shows black. The logs say when sound is detected or silent (as C++). `enable`
defaults to true (HyperHDR: false): the capture only runs while a music effect does, and the default output needs no
microphone permission, so music effects work without setting anything up.

`Backbloom.Audio.Windows`: WASAPI, shared mode, event-driven (waits at most 50 ms), the device's mix format.

| `soundEffect.device` | Captures |
|---|---|
| `default` (the schema default) | what the default output device plays (loopback), following Windows when the default output changes (checked every 2 s) |
| `loopback:<endpoint id>` | what that output device plays |
| `input:<endpoint id>` | that recording device (microphone, line in) |
| a device name (HyperHDR stored waveIn names) | the recording device with that name (waveIn names are cut to 31 characters); imports replace such names with `default` |

Endpoint ids (`{0.0.0.00000000}.{guid}`) survive restarts and renaming, unlike names. A loopback stream delivers
nothing while nothing plays; that time is fed to the analysis as silence. A lost device (unplugged, disabled, format
change) is reopened every second. Interop is TerraFX, as for screen capture: COM calls without wrappers, so the read
loop allocates nothing (NAudio would add a dependency with its own allocation per buffer; CsWin32 needs per-API
generator configuration).

`serverinfo.sound` is `{ active, device, sound_available: [ids], sound_devices: [{ id, name, kind }], running, error }`:
`sound_devices` (names for display), `running` (sound arrives) and `error` (why the device failed, in words for the
failures a user can fix: microphone access blocked in Windows privacy settings, device disconnected or in exclusive use)
are additions; `active` is false while capture is disabled or the device fails. It is pushed as `sound-update` when the
capture starts, stops, fails, recovers, or hears sound or silence. Each capture session reports through its own
observer, so a session being replaced (the device changed) can't mark the new device as failed. The UI's Effects &
Music page shows the names, grouped into outputs and microphones, and the error.

HyperHDR could only capture recording devices, so its `soundEffect.device` is a microphone name because nothing else
was offered; music on a PC plays through its output, so imports (`LegacyMigrations.UseDefaultSoundDevice`) set it to
`default`. Ids chosen in this version (`loopback:`, `input:`) are kept.

Measured (Release, owner's 168 LEDs, file driver to the null device, no clients, smoothing off as the owner has it):
"Rainbow swirl fast" at its full 52.6 steps/s 0.62 % of one core; music mode (pulse, default output in loopback while
music played, 21.5 blocks/s) 0.16 %.

## LED drivers and discovery

Every driver of the C++ build is ported (sources/led-drivers); the settings keys and schemas are the C++ ones
(`Backbloom.Devices/Schemas`, reformatted), so imported configurations keep working. A driver only speaks its protocol
(`ILedDriver`: open, write a frame, close); `LedDeviceRunner` does the lifecycle once for all of them.

| Type | Group | Transport | Notes |
|---|---|---|---|
| `wled` | network | REST + UDP | DRGB/DNRGB; discovery: mDNS `_wled._tcp`; reads WLED's power settings on connect and its status every 10 s while connected ([telemetry](#performance-recording)) |
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
`setInError` + `setupRetry`). Network drivers (WLED, Hue, Home Assistant, Yeelight, and those with
`RetryPolicy.Default`, which also covers the SyncLight HID strip) don't give up after their `maxRetry` quick retries
as the C++ code did: they go on every 30 s (`RetryPolicy.ThenEvery`; logged once, then at debug level), so a
controller that was away for a while (or a USB strip plugged in again) is used again by itself. Serial and file
devices still give up, so the UI describes their `maxRetry` differently (`edt_dev_max_retry_serial`). JSON/REST drivers
write their JSON with sorted keys, as `QJsonDocument` did.

**Setting tiers.** Every property of a driver schema (and of `device`, `mqtt`, `webConfig`) has a tier in `access`
(docs/ui-guidelines.md): none for what is needed to connect (the address or port, the lamps or lights, the universe of
Art-Net/E1.31), `"advanced"` for fine-tuning the UI puts under More options (WLED brightness, RGBW, network ports, Hue
streaming and black handling, transitions), `"expert"` for what only expert tools show (`maxRetry`, ICE parameters and
white calibration, `cid`, serial handshakes and delays, timeouts, `verbose`, `debugLevel`, `refreshTime`). The UI makes
color order expert for WLED and DDP, which order the colors themselves. Titles are keys of `ui/src/schema/text.ts`;
Backbloom's own keys (`edt_dev_spec_hueBridge_title`, `edt_dev_spec_lampColorModel_title`, …) replace C++ keys whose
text didn't fit. Serial devices default to the `auto` port (sp616e and Skydimo had the Linux `ttyACM0`).

**Host names** (`HostResolver`, the default `DriverContext.ResolveHost`): the system resolver (3 s at most), for a
`.local` name it can't resolve an mDNS A query of our own (`MdnsService.ResolveAsync`, 1.5 s), and when both fail the
last address the name had. Not in the C++ code: Windows' resolver once kept failing for a WLED controller's `.local`
name in the running process ("No such host is known", HyperHDR showed the same) while a browser and a restarted
process resolved it at once. The WLED driver resolves its host once per connection and sends both its HTTP requests
and its stream to that address, so `HttpClient` never resolves the name itself (requests carry the address as their
`Host`, which WLED ignores). The last address is only a fallback: after a DHCP change it may belong to another device,
and requests reach that one until the name resolves again.

**RGBW.** `RgbwConverter` (classic 8-bit algorithms, HyperSerial calibration tables) and `InfiniteRgbwEncoder`
(stateful, dithered) reproduce the C++ float operations in the same order and match the exported fixtures exactly.

**Libraries** (all permissive, maintained, pinned in `Directory.Packages.props`):

| Package | License | Used for | Why |
|---|---|---|---|
| BouncyCastle.Cryptography 2.7 | MIT | DTLS 1.2 PSK client (Hue Entertainment) | .NET has no DTLS; BouncyCastle is the standard managed implementation and needs no native OpenSSL |
| MQTTnet 5.2 (+ MQTTnet.Server in tests) | MIT | `Backbloom.Mqtt.MqttConnection` | the .NET Foundation MQTT client; tests run a real broker in process |
| HidSharp 2.6 | Apache-2.0 | HID enumeration and reports, serial port names/USB ids | managed, cross-platform (hid.dll, hidraw, IOKit), so no platform project is needed; TerraFX would cover Windows only and need a Linux/macOS port later |
| System.IO.Ports 10.0 | MIT | serial I/O | the platform's serial API |

**Discovery** (`Backbloom.Discovery`). Passive: it only sends queries (mDNS PTR, SSDP M-SEARCH, the LIFX/WiZ broadcast
probes) and lists what answers; nothing found is configured or contacted by the discovery itself. It runs only when
asked (`leddevice discover`: the UI's Search my network in Add device, or Search in a device's connection settings,
explained once before the first network search) and listens nowhere in between: every query goes out from `QuerySockets`,
one UDP socket per local IPv4 address bound to that address and an ephemeral port, closed when the query ends. The
answers come back to that port (Windows Firewall lets unicast answers to a multicast or broadcast query in for a few
seconds without an inbound rule), so nothing is bound to 0.0.0.0 or to a well-known port. Measured on the
development machine: at startup only `127.0.0.1:<port>` (TCP); during a search one ephemeral UDP port per interface
address; afterwards loopback only again.
- mDNS is implemented here (about 900 lines: DNS message codec, cache with TTLs and goodbyes, responder) instead of using a
  library: the maintained .NET options are browse-only (Zeroconf) or unmaintained since 2019 (Makaretu.Dns.Multicast),
  and the protocol subset needed is small and fully testable without sockets (`IMdnsTransport`). A browse opens its own
  `UdpMdnsQueryTransport`: queries from a port other than 5353 are RFC 6762 legacy unicast queries and carry the QU
  bit, so responders answer the querier directly. The responder transport (`UdpMdnsTransport`: UDP 5353 shared with
  the OS responder, 224.0.0.251 joined per interface) exists only while the web UI is advertised. Own addresses are
  never reported.
- The web UI is advertised as `<host>:<port>._backbloom-http._tcp.local` only while the server
  listens on the network (remote access on, or `--bind` with a network address); a goodbye is sent when it stops.
- SSDP: one M-SEARCH (C++ message, MX 1) per interface and every answer within 3 s, headers parsed like `SSDPDiscover`.
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

## Desktop shell (port step 6c)

Port of `sources/app` (main, AppDaemon, SystrayHandler), `systray` and `suspend-handler`, reorganized: the
platform-neutral parts live in `Backbloom.Shell`, the Windows parts in `Backbloom.Platform.Windows/Desktop`, and `App`
wires them (`DesktopRegistration`).

| | |
|---|---|
| Single instance | One app per data directory (C++: one `backbloom.exe` per machine, a second start failed). The first instance holds `backbloom.lock` in the data directory exclusively (released by the OS even after a crash) and listens on a named pipe derived from the directory (current user only). A second start with the same directory asks it to show its window (headless: the second process opens the UI in the browser) and exits; `--quit` asks it to quit and waits. Instances with different data directories run side by side. |
| Tray | `Shell_NotifyIcon` (version 4) with the Backbloom icon (`branding/backbloom.ico`, embedded; the best size is picked from the .ico). Menu, rebuilt on each open (`TrayMenu`): **Open Backbloom** (default, also a click on the icon), **Lights** (`ALL`), **Screen sync** (removes colors and effects, capture on; checked while the screen shows or waits for its first frame), **Music** (the music effects), **Effect** (the six effects), **Color** (the C++ colors) -- music, effects and colors run at priority 1 until something else is chosen, as C++ did; the running one is checked --, **Pause for 30 minutes** (then *Resume*; a switch elsewhere ends the pause), **Start with Windows**, **Quit**. The icon comes back after an Explorer restart (`TaskbarCreated`). C++'s "Instances" submenu is gone (one pipeline). |
| Window | A plain Win32 window hosting the UI in WebView2 (`Microsoft.Web.WebView2.Core` only, BSD-3; no WinForms/WPF, their assemblies are kept out of the output by `Directory.Build.targets`). Dark title bar, the UI's background until the page draws, placement (normal bounds + maximized) in `window.json` (atomic write; restored only when it is on a monitor). Closing it destroys the window and its browser processes (no memory held while hidden); the app stays in the tray. Links to other sites open in the default browser. Without the WebView2 runtime (`GetAvailableBrowserVersionString`) or when it fails to start, the UI opens in the default browser. Profile in `<data>/webview`. |
| Autostart | `RegistryAutostart`: value `Backbloom` in `HKCU\...\CurrentVersion\Run` (as C++), command `"<exe>" --no-window` plus `--data-dir` when it isn't the default; enabled only when the entry starts this executable (also C++'s bare-path entries). Off by default: only the user turns it on (tray menu, the first-run setup's checkbox, `setup autostart`). The key is a constructor parameter; the tests use a scratch key and never write the real Run key. |
| Power and session | `WindowsSystemEvents` (hidden top-level window: `RegisterSuspendResumeNotification`, `RegisterPowerSettingNotification(GUID_SESSION_DISPLAY_STATUS)`, `WTSRegisterSessionNotification`, `WM_ENDSESSION`) raises `ISystemEvents`; `PowerPolicy` (port of `InstanceManager::hibernate`) reacts: sleep -> `ALL` off, wake-up -> on 3 s later; locked or display off with `general.disableOnLocked` -> `SYSTEMGRABBER` off (the screen source leaves, so the LEDs go dark unless a color or effect shows: an effect chosen by the user keeps running while locked, as in C++; without this setting the screen still leaves 800 ms after the lock screen stops its frames), unlocked and display on -> capture on 3 s later, and the screen shows again with its first frame. A lock in a remote desktop session is ignored (as C++). |
| Startup | `general.disableLedsStartup` starts with `LEDDEVICE` off (engine, unchanged); `--no-window` keeps the window closed, except until the first-run setup was completed (the window always opens then, so the setup is seen). |
| Logging | Console (when there is one), `logs/backbloom-yyyyMMdd.log` in the data directory and the UI's log view, at the level of the `logger` section (silent = errors only, warn, verbose = info, debug), applied at once when it changes. One plain-text line per message (time, level, category, rendered message template; exceptions indented below), written by a background thread; a new file per day and per 10 MB, the newest 10 kept. A failed start or a crash (unhandled exception) is appended there as a `FATAL` entry by `CrashReport` (directly, so it is recorded before logging exists and when the process ends; with the [recent performance](#performance-recording) when there is any), printed to the console if there is one, and shown in a message box (`ErrorDialog`) unless `--service`. |
| Shutdown | Quit, `--quit` and Ctrl+C stop the host: WebSocket clients are closed at once, the engine switches every controller off (black frame, then closed), then the tray icon goes. Where Windows ends the process as soon as a notification returns (`WM_ENDSESSION` at logoff/shutdown; console close and shutdown signals), the handler stops the host and waits up to 4.5 s for the LEDs (`GracefulShutdown`). C++ didn't handle the session end. |
| Settings | Crash-safe as before: every change is one SQLite transaction (rollback journal, `synchronous=FULL` by default); `window.json` is written to a temporary file and moved over. |
| Packaging | `backbloom.exe` is a `WinExe` with the Backbloom icon; the built UI is copied next to it (`ui/`, build and publish) when `ui/dist` exists. `packaging/build.ps1` publishes a self-contained ReadyToRun folder and packs the Velopack installer with automatic updates; see [packaging/README.md](../packaging/README.md). |

Design choices:

- **WebView2 directly, not Photino.NET.** The tray needs native code on every OS anyway (`Shell_NotifyIcon`,
  `NSStatusItem`, StatusNotifierItem), and the window only shows one local page, so a cross-platform window library
  saves little. Hosting WebView2 in our own Win32 window gives what the shell needs and Photino doesn't: hiding vs.
  destroying on close, placement with `Get/SetWindowPlacement`, the end-session message, one message loop for tray,
  notifications and window, no second native library. macOS/Linux implement `ITrayIcon` / `IAppWindow` /
  `IAutostart` / `ISystemEvents` in their own projects (WKWebView/WebKitGTK, or Photino behind `IAppWindow`).
- **Interop**: TerraFX.Interop.Windows (already used by capture) for user32/shell32/dwmapi, `[UnmanagedCallersOnly]`
  window procedure, `LibraryImport` for the two wtsapi32 calls TerraFX lacks. The WebView2 wrapper uses built-in COM
  interop, so `Platform.Windows` is no longer marked AOT-compatible.
- **One UI thread** (STA) owns every window: WebView2 and the menu loop need a message pump; it has a
  `SynchronizationContext`, so `await` in the window code returns to it.

## First run, HyperHDR and network access

A first install must not take over the lights or the network by itself: the HyperHDR import used to drive the LEDs
at once (two senders, flickering, while HyperHDR still ran) and the installed copy listened on every interface
(an unexplained Windows Firewall prompt).

**Lighting gate** (`Backbloom.Engine/LightingGate`). The output is held (`ComponentStates.SetHeld`: the controllers stay
closed, nothing is sent; capture, mapping and the preview keep running, the protocol's switches keep the user's choice)

- until the first run is completed: the meta key `first_run_completed` (`ISetupState`, in `settings.db`'s `meta`, not
  in a settings section, so backups, restores and imports don't touch it), set by "Turn on lights" in the first-run
  setup or in the banner shown after "Look around first" (`setup start`). A new data directory and every first-start import wait for it; while it waits the window opens
  even with `--no-window`. An update keeps the key, so it never holds a configured setup (or an autostarted one);
- while HyperHDR (`hyperhdr.exe`) or Hyperion (`hyperiond.exe`) runs: `OtherAppMonitor` asks `IProcessList` at start
  and every 2 s (`WindowsProcessList`: one Toolhelp snapshot, image names only, no handles; elsewhere
  `Process.GetProcessesByName`). When it quits the output follows within 2 s; when it starts later Backbloom switches
  its controllers off and lets go.

The gate starts before the engine, so a held start never opens a controller. The startup effect waits for the
release and runs then, as if the app had started at that moment.

**First-run setup** (UI, `ui/src/pages/welcome`). Shown instead of the app until `firstRunCompleted`; three steps:

1. Welcome: what Backbloom does; after an import, what came over from HyperHDR (the device, the number of LEDs, the
   screen sync, color and effect settings), on a new install what to check before starting. While HyperHDR runs, two
   sentences say that Backbloom waits and takes over by itself when it quits. The technical summary (monitor, frame
   rate, startup effects, the imported database's path) shows only with expert tools on.
2. Your lights: the devices of the layout (the test device of a new install, `file`, is not listed), or "No device
   yet" with Add device: the same dialog as on Devices (`AddDeviceDialog`, with the network search and its
   explanation). The first real device replaces the test device. Devices added here are layout edits, saved on the
   Layout page.
3. Turn on: "Your lights stay as they are until you turn them on here", the unchecked Start with Windows box and Turn
   on lights (`setup start`; while HyperHDR runs it takes over once it quits). The app then opens on Devices when there
   is still no real device, on Layout while no layout is saved (or the setup added a device), else on Home.

"Look around first" opens the app without turning the lights on: a banner says they are waiting and offers Turn on
lights, and Home shows "Waiting — turn on your lights to start" (or "Waiting for HyperHDR to quit").

**Network access** (`Backbloom.App/RemoteAccess`, setting `network.remoteAccess`, default off, also for imports). Off:
Kestrel listens on 127.0.0.1 only and nothing is advertised. On: an endpoint per IPv4 interface address
(`<address>:<port>`) comes from a reloadable configuration (`NetworkEndpointConfiguration`, Kestrel
`Configure(..., reloadOnChange: true)`), so they are bound and unbound live and follow address changes
(`NetworkChange.NetworkAddressChanged`); the loopback endpoint is configured in code and never touched (the app's own
window stays connected). Specific addresses rather than 0.0.0.0, so they never overlap the loopback endpoint and the
tests (127.0.0.2 as the "network" address, `RemoteAccessOptions`) exercise the same binding. Unbinding waits for open
connections on those endpoints to end (a few seconds). The mDNS responder starts and stops with them. `--bind` fixes
one address instead and the setting has no effect. The UI (Settings → Remote access, with the password, app keys
and ports) explains what turning it on does (and that Windows asks next, Private networks only) before it sets the
setting. Without a password the endpoints stay closed even when the
setting is on (see [Access](#access-this-computer-and-other-devices)); setting one opens them at once.

**Protocol.** `setup` (Backbloom addition): `getstate` for any authorized session; `start` `{ autostart? }`,
`autostart` `{ enabled }` (the welcome, the tray menu and Settings → General → Start with Windows),
`acknowledge` `{ topic: "discovery" }` for admins. Each replies with
`{ firstRunCompleted, outputHeld, otherApp: { name, executable } | null, importedFrom, autostart: { available, enabled },
remoteAccess: { enabled, fixedByCommandLine, port, urls, advertised }, passwordSet, acknowledged }`, which the
`setup-update` event pushes on every change (also when the password is set).

## MQTT integration

Port of `sources/mqtt` on top of the shared `MqttConnection` (MQTTnet), in `Backbloom.Server/Mqtt`:

| | |
|---|---|
| Connection | The `mqtt` section (Settings → Integrations): `enable`, `host`, `port`, `username`, `password`, `is_ssl`, `ignore_ssl_errors`, `maxRetry` (5 s apart, then it gives up; C++: 10 s). Reconnects when the section changes; the first attempt runs in the background so an unreachable broker doesn't delay the start. |
| JSON-RPC | Requests on `<topic>/JsonAPI` (`custom_topic`, default `Backbloom`; HyperHDR used `HyperHDR`): one request object or an array of them, run with the rights of a local client (C++ `localConnection`), every message the session produced is published as one JSON array on `<topic>/JsonAPI/response` (QoS 2). Unreadable JSON gets `{ success: false, error }`. `disableApiAccess` doesn't subscribe at all (as C++). |
| Availability (new) | `<topic>/status`: `online` (retained) after every connect, `offline` as the connection's last will and published before a clean disconnect. |
| Home Assistant (new) | With `homeAssistantDiscovery` (new setting, default on): a retained discovery config on `homeassistant/light/backbloom_<id>/light/config` (JSON schema, RGB, brightness, effect list "Screen sync" plus every effect and music effect, device = `general.name`), commands on `<topic>/light/set` (`OFF` -> `ALL` off; `ON` -> on; a color -> static color at priority 1 scaled by the brightness; effect "Screen sync" -> colors cleared, capture on; any other effect -> runs at priority 1) and the retained state on `<topic>/light/state`, published on every change from any client. Switching discovery off removes the entity (empty retained config). |

## LED controllers and the layout (port step 6b)

One capture, one mapping pass, many controllers on one clock: users think in strips, and a strip is wired to an output
of a controller. There are no per-controller pipelines ("instances").

**Data model.** The `layout` setting (the UI's `LayoutSpec`, `ui/src/layout/types.ts`) is the source of truth:

```
{ version: 1,
  room: { width, depth, height, screen: { x, y, width, height }, furniture },
  controllers: [{ id, name, enabled?, kind?, host?, outputs: [{ index, start, length, pin? }],
                  device: { type, colorOrder, refreshTime, ...driver settings } }],
  strips: [{ id, name, controllerId, output, ledCount, path: [{ surface, x, y, at?, next? }],
             dark?: [{ start, count }], depth?, blend?, brightness? }] }
next: { kind: edge, from, to } | { kind: whole } | { kind: area, area } | { kind: none }
    | { kind: effect, effect } | { kind: color, color: [r, g, b] } | { kind: gap }
profiles?: [{ id, name, apps: [executable names], game?: bool, sections: { <strip id>: [section | null per section] } }]
```

Strips are placed in a schematic room (meters): the screen hangs on the front wall, and a strip is a path of two or
more points on the walls (`front`, `left`, `right`, `back`), the `ceiling` or the `floor`, each in that surface's own
coordinates. The points split the strip into sections; a point's `next` is the section to the following point (the
last point's is ignored). LED k sits at position k + 0.5 along the path: points matched to the real lights carry their
LED position `at`, the others are spaced by length, and `gap` sections (no LEDs: a cut corner, a cable) count no length,
so the LEDs spread over the other sections. An `edge` section repeats the stretch of the screen's outline from `from`
to `to` (outline positions 0..4 clockwise from the top-left corner, one per edge, wrapping around): its LEDs spread
evenly over the stretch, each `depth` (default 0.1) screen heights into the picture and as wide along the edge as the
LED spacing there times `blend` (default 1.2). Without `next` (auto) a section is the edge stretch it faces: both ends
seen straight on against the front wall (depth dropped), each at the nearest point of the outline, the short way
round. `whole` LEDs show the whole picture (averaged once for all of them, and slowed down on the frame loop by
`ScreenAverageFilter` before the color settings: an exponential filter with a 500 ms time constant, because a
whole-screen average is a pale color whose hue swings with small changes in the picture and the saturation gain makes
the swings visible), `area` LEDs one area, `none` LEDs stay off, as do LEDs in `dark`.
`effect` and `color` LEDs play something of their own (a zone, see below), whatever the rest of the lights show.

`Backbloom.Settings/Layout`: `LayoutSpecification.Normalize` validates it (rejecting with `layout.<path>` errors, like
every setting) and fills in each controller's `device`; `Compile` turns it into one sampling area per LED per
controller with the same math as the UI's `ui/src/layout/room.ts` and `compile.ts` (both are checked against
`tests/fixtures/layouts/ui-multi-controller.*.json`; the C# output matches bit for bit, the test allows one step of the
4-decimal precision); `ControllerSetup` is what the engine runs. Compiling runs only when the layout changes, never per
frame. Rules (the errors of the UI's `validate.ts`; its warnings aren't enforced): a room 0.5 to 50 m each way, a
screen of positive size on the front wall, `furniture` desk, tv or none; at least one controller; unique controller and
strip ids; a controller's outputs have unique indexes, at least 1 LED each, and follow each other from LED 0 without
gaps or overlaps, so its LED count is the sum of its outputs (at most 10,000); a strip has a whole `ledCount` of at
least 1, a path inside the room with a length above 0 outside its gaps, whose `at` positions lie within the strip and
don't go backwards, sections of a known kind (`edge` with numeric `from` and `to`, `area` with numeric bounds, minimum
not past maximum; an area reaching outside the picture is cut to fit; `effect` with a non-empty name, which needn't
exist in the catalog; `color` with three whole numbers 0..255), `dark` ranges within the strip, `depth` in
(0, 1], `blend` above 0 and `brightness` 0..1, names an existing controller and output (one strip per output) and has
at most as many LEDs as the output (fewer: the rest stays dark, the UI warns); each `device` is validated against its
driver's schema (defaults filled in); `profiles`, when present, is a list of profiles with unique non-empty ids, a
name, `apps` as a list of non-empty program names and `sections` as an object of lists, whose entries are null (auto)
or valid sections other than `gap`. A profile's list for a strip that isn't there, or whose length isn't the strip's
section count, is only a warning in the UI and is ignored when the profile is applied, as are profiles without
programs. Properties the backend doesn't use (`kind`, `host`, `pin`, `name`, and the `seat`, `show` and `area` of the
earlier, never released room model) are kept.

**Zones.** The LEDs of `effect` and `color` sections are disabled in the compiled `leds` (the screen's mapping skips
them) and form zones (`ControllerSetup.Zones`, `compileZones`): one per distinct role (the same effect, or the same
color) across strips and controllers, with each LED's index among all controllers' LEDs and the rect `auto` would
give it (`CompileStrip(strip, room, zone: true)`), so an effect's picture flows across the zone as if it were the
screen. Dark LEDs and LEDs past their output aren't in a zone. Checked against `ui-multi-controller.zones.json`
(indices exactly, rects within 1e-4). The engine plays them over every frame (`ZoneLayer`):

- an `effect` zone runs its own `EffectSource`, mapped with the zone's rects (with the user's mapping type) or, for
  effects that set LED colors, with one color per zone LED; a music effect opens the sound capture like a visible one
  and stays dark without sound; an effect name that isn't in the catalog leaves the zone dark (logged once);
- a `color` zone shows its color;
- effect zones go through the color settings like effects, color zones past them like static colors; then they are
  written into their LEDs of the frame handed to `OutputStage` (smoothing, if on, and strip brightness apply to them
  too);
- zone effects step without waiting (`EffectSource.TryStep`, catching up on steps missed since the last call so they
  keep their speed): with every frame of the visible source; while a visible effect waits for its next step (it
  returns early for a zone step, `ILightSource.TryRender`'s `wakeAtMs`); and while nothing new comes (a static color,
  no source, an inactive screen), when the frame loop wakes for the zones' next step (in slices of 20 ms, so a new
  source still shows at once) and sends the last frame again with the zones' new colors. A screen capture isn't cut
  short for them (the frame pacer would skip a frame), so while the screen shows they step with its frames;
- while zones exist the controllers stay on without a source (the other LEDs are black); zone effects pause while the
  output is off or held;
- a layout change rebuilds the zones; an effect whose zone is still there keeps running (same instance).

**Profiles.** `layout.profiles` give the strips' sections other roles while a program runs (a game, a video player):
`LayoutSpecification.ApplyProfile` (`applyProfile` / `stripInProfile` in `ui/src/layout/profiles.ts`) puts a
profile's sections onto each strip it lists with a list as long as the strip's sections (null: auto; gaps stay) and
`Compile(spec, profileId)` compiles that (checked against `ui-multi-controller.witcher.*.json`). `ProfileMonitor`
(Engine) asks `IProcessList.FindRunning` for each profile's programs, in list order, at start and every 2 s while the
layout has profiles: the first profile one of whose programs runs (names compared case-insensitively) is applied with
`LightingEngine.ApplyProfile`, which compiles the stored layout for it and swaps rects and zones without saving
anything; controllers whose driver settings and LED count didn't change keep running (a profile can't change them).
`profile select` chooses one by hand until it is set back to automatic (not saved: a restart chooses by the programs
again); removing that profile from the layout goes back to automatic. Later layout saves are compiled for the active
profile.

**Ambient and Game mode.** The `modes` section (new, `Schemas/modes.json`) holds the mode chosen in the app (`mode`:
`ambient` or `game`), the `game` section (new, `Schemas/game.json`) what Game mode changes (`time_ms`, `stiffness`,
`damping`, `saturationGain`, `luminanceGain`, `fps`; the defaults are the UI's Intense preset, `springFor(150)`, and
60 fps). Two sections, so that saving Game's values can't overwrite a mode chosen meanwhile. `ModeMonitor` (Engine)
decides which mode applies: the saved one, except Game while the active profile is a game's (`game: true`); `set`
Ambient while such a profile runs keeps Ambient until another profile (or none) runs, and `set` Game while it still runs
only turns Game mode back on (`dismissedGame` says which game it is off for). Changes are announced under its lock, in
order. It calls
`LightingEngine.ApplyGameMode`, which builds the color processor and the smoothing from `GameTuning` applied to the
saved `color` and `smoothing` sections (nothing is saved; smoothing is on and the minimum glow off in Game mode, and
the `SMOOTHING` component
follows the applied setting), shows only the screen (`Muxer.ScreenOnly`, not in the C++ code: colors, pictures and
effects keep their place and timeouts but aren't selected, so they come back in Ambient; a held effect doesn't render,
a held music effect keeps its sound capture), and tells `CaptureModeFollower` (App), which reconfigures the screen
capture with at least `game.fps` (only when the
resulting `CaptureSettings` differ, since each new configuration restarts the capture). The follower exists because
the monitor depends on the engine, which depends on the capture source.

The store keeps the other sections consistent with it, in the same transaction:

| Saved | Effect |
|---|---|
| `layout` | `leds` = every controller's LEDs one after the other; `device` = the first controller's; the `controllers` table mirrors the layout (ids 0..N-1, name, enabled, device; backups and HyperHDR-format exports keep working). A controller sent without `device` keeps the one it had (by id), else (first save after an import) the stored controller at its position, else the default (`file`). `leds` sent along is replaced by the derived one. |
| `device` (old clients) | changes the first controller of the layout |
| `leds` (old clients, HyperHDR's LED editor) | unchanged: nothing happens. Different, with a one-controller layout: the layout is removed and `leds` + `device` describe the LEDs again; with several controllers: rejected ("save the layout instead") |
| neither (a HyperHDR import, no layout saved yet) | the legacy controller: `device` driving `leds`, id `controller-1` |

**Migration.** Nothing is rewritten: a configuration without `layout` (every HyperHDR import) runs as one controller
from `leds` + `device`, as before; the first layout saved by the UI takes over (the UI builds it from those two and
keeps the device). A layout stored by the previous version (without devices, one controller) gets the stored
controller's device on load; an invalid stored layout is ignored with a warning (the LEDs follow `leds` + `device`).
That includes layouts in the earlier, never released format (strips made of screen-edge `segments`, no room): there is
no conversion, the UI starts a new room layout from `leds`. Imported HyperHDR instances beyond the first are still
stored as controllers but get LEDs only when a layout uses them.

**Engine.** The mapping runs once per frame over the union of all controllers' LEDs (the pipeline's `LedLayout` is
`leds`), so N controllers cost one `ImageToLedMap` pass, one color pass and one smoother, exactly like one controller
with that many LEDs; per-controller rect sets would only add work. `DeviceManager` gives controller N the slice that
follows controllers 0..N-1 (`Submit` hands each runner a `ReadOnlySpan` slice; no allocation), every controller gets
every frame from the same thread, and each runner sends on its own thread, so a slow, unreachable or retrying controller
only drops its own frames (`MultiControllerTests`: a driver whose writes take 300 ms doesn't delay the others or
`Submit`). Strip brightness is a per-LED gain applied after smoothing and the anti-flicker filter (`OutputStage`;
null when every strip is at 100 %), so the preview shows it too. Each runner measures the time from `Submit` until the
driver had written the frame (`FrameStats` latency).

**Protocol** (additions; old single-controller clients keep working):

| | |
|---|---|
| `config getconfig` / `setconfig` | `layout` with every controller's `device`; `leds` (all LEDs) and `device` (first controller) as above |
| `serverinfo.controllers`, event `controllers-update` | `[{ id, name, type, enabled, running, state, status, start, ledCount, strips: [{ id, name, start, length, brightness }] }]`; `start` is the controller's first LED in `leds` and in the LED stream, a strip's `start` counts within its controller; the event follows layout changes and state changes (ready, retrying, ...) |
| `performance-update` | one LED report (type 3) per controller: `id` = position, `param1` updates/s, `param2` written, `param3` submitted, `param4` dropped since the last report, plus `controller`, `controllerName`, `ledCount`, `state`, `latencyMs` |
| `leddevice identify` `blink` | `params: { blinkIndex, controller?, hold? }` or `{ stop: true, controller? }`: `controller` is an id (or a position); without it `blinkIndex` counts over all LEDs (old clients, one controller: unchanged). `hold` keeps the LED cycling red, green, blue until the next blink, a stop or 120 s (`BlinkPattern.HoldTimeout`). Replies `{ controller, blinkIndex, hold }` (a stop: `{ controller, stopped }`); an LED that doesn't exist, a controller that isn't running, or a held output (first run, HyperHDR running) is an error. See [docs/protocol.md](../docs/protocol.md#finding-an-led-leddevice-identify-leddevicetype-blink) |
| `ledcolors` stream | `leds` covers every controller's LEDs; each message and the `ledstream-start` reply carry `controllers: [{ id, start, length }]` |
| `serverinfo.profile`, event `profile-update` | the active profile `{ id, name, manual, game }` or null; the event carries `{ profile }` |
| `serverinfo.mode`, event `mode-update`, `mode` | `{ mode, saved, gameProfile, dismissedGame }`; `mode getstate` (`{ mode }`), `set` `{ mode: "ambient" \| "game" }` (API access; replies `{ mode }`). See [docs/protocol.md](../docs/protocol.md#modes-mode) |
| `profile` | `getstate` (`{ profile }`), `select` `{ id: string \| null }` (null: choose by the running programs; replies `{ profile }`), `programs` (admins: the programs with a window, `{ programs: [{ exe, title }] }`, one per executable, sorted, from `IProgramList`: `EnumWindows` on Windows without tool, owned or cloaked windows, `Process.MainWindowTitle` elsewhere). See [docs/protocol.md](../docs/protocol.md#profiles-profile) |

**UI** (`ui/`): the Layout page saves layouts with any number of controllers (`saveLayout` sends `layout`, plus the
compiled `leds` for backends that don't compile layouts); the Devices page shows one card per controller ("device" in
the UI) with its outputs (LEDs numbered from 1), an on/off switch, its state and its connection settings (the driver's
schema form plus `device`'s shared settings, collapsed, saved with the layout). Add device takes WLED (outputs read
from the device) or any other device type, with Search my network for the address, and then points to the Layout
page. Lamp devices (Home Assistant, Zigbee2MQTT, LIFX, WiZ, Yeelight) get one LED per lamp in their list: the UI keeps
the output's length equal to it instead of asking. The `file` driver is offered only with expert tools on. The Layout
page has two modes, Place (where strips run, their outputs and LED counts, matching with the lights, the test pattern;
guided steps while no layout is saved) and Customize (what each part shows, per-strip brightness, turned-off LEDs,
depth and softness, profiles). Matching and Find an LED (an expert tool, LEDs numbered from 1) light one LED with
`leddevice identify` `blink` on that controller; Home lists the LED updates and delay per controller when there are
several.

Measured (Release, synthetic source at 60 fps, smoothing off, file controllers writing to the null device, process CPU
over 30-60 s, 24 logical cores, other load on the machine): the owner's 168 LEDs on one controller 0.8-1.1 % of one
core; the same plus two test strips (30 and 20 LEDs) on two more controllers 0.9-1.9 %; noise dominates the
difference. `PipelineProfile` (explicit test, in process): 0.5 % (1 controller) vs 0.5-1.3 % (3 controllers); every
controller written at 60.0 (50.0 with smoothing) frames/s. A live run with three file controllers (91 + 77 LEDs on A,
30 on B, 20 on C) wrote exactly 168 / 30 / 20 LEDs per line, each line equal to its own slice of the live stream's
frames and never to another position, all three at 60.06 updates/s, latency 0.03 ms.

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
| `adjustment`, `ledcolors` ledstream-start/stop, `leddevice` identify/discover/getProperties, `logging`, `instance` switchTo | done (several controllers: see [LED controllers and the layout](#led-controllers-and-the-layout-port-step-6b)) |
| `effect` (`priority` 1..253, `effect.name`, `duration`, `origin`) | done (see [Effects](#effects)); `effect.args`, `pythonScript` and `imageData` are rejected (C++ rejected arguments too) |
| `setup` getstate, start, autostart, acknowledge (+ `setup-update`) | new (see [First run, HyperHDR and network access](#first-run-hyperhdr-and-network-access)) |
| `profile` getstate, select, programs (+ `profile-update`, `serverinfo.profile`) | new (see [LED controllers and the layout](#led-controllers-and-the-layout-port-step-6b)) |
| `mode` getstate, set (+ `mode-update`, `serverinfo.mode`) | new (see [LED controllers and the layout](#led-controllers-and-the-layout-port-step-6b)) |
| `image`, imagestream | not yet ("Command not implemented") |

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
8. Access: this computer always has full access and other devices always sign in; there is no factory password, and
   HyperHDR's `network` access options are gone (see [Access](#access-this-computer-and-other-devices)). `getstate`
   of `setup` needs a signed-in session like every other command.

Also: `logout` returns the connection to the rights it had before signing in, and the identify blink is applied
before the controller's color order (so it is really red, green, blue on GRB strips).

## Settings storage and HyperHDR import

**Sections and validation.** Every settings type the C++ build kept is a section: `general`, `logger`, `device`,
`color`, `smoothing`, `systemGrabber`, `blackborderdetector`, `foregroundEffect`, `backgroundEffect`, `soundEffect`,
`webConfig`, `effects`, `systemControl`, `network`, `ledConfig`, `leds`, `mqtt`, `layout`, plus Backbloom's `modes`
and `game` (Ambient and Game mode). Their schemas are the C++ files (`sources/base/schema`, copied into
`Backbloom.Settings/Schemas`; the additions are `mqtt.homeAssistantDiscovery`, `network.remoteAccess`, `modes` and `game`), the LED driver schemas come from `Backbloom.Devices`, and defaults exist only there. The in-house validator stays: the schemas use the draft-03
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

| HyperHDR | Backbloom .NET |
|---|---|
| global types (`general`, `logger`, `network`, `webConfig`, `mqtt`, `systemGrabber`, `soundEffect`, `layout`) | pipeline sections |
| per-instance types of the first instance (`leds`, `ledConfig`, `color`, `smoothing`, `blackborderdetector`, `foregroundEffect`, `backgroundEffect`, `effects`, `systemControl`) | pipeline sections |
| `device` of instance N (in instance order), name and enabled flag from `instances` | controller N; controller 0 is the `device` of `getconfig`/`setconfig` (with a `layout`, the table mirrors its controllers) |
| per-instance settings of further instances | not imported (reported) |

Without a `layout` the engine drives controller 0 (`device` + `leds`); a saved layout describes every controller (see
[LED controllers and the layout](#led-controllers-and-the-layout-port-step-6b)).

**Import** (`SettingsImporter`, `LegacyMigrations`, ported from InstanceConfig, SettingsMigration, LegacyMigration and
AccessManager/AuthTable): reads HyperHDR v17 to 24-beta databases (`hyperhdr_instance` column, user `Hyperhdr`); the
source is copied to `imported/` and only the copy is opened.
Rules, all idempotent and logged one line each (never with secrets):

- format upgrades by `general.version` (HyperHDR upgradeDB up to 6: backlight threshold steps, pause while locked);
- removed settings types are dropped: proto/flatbuffers/raw UDP/boblight/JSON servers, forwarder, the capture-card stack
  (`videoGrabber`, `videoControl`, `videoDetection`, `automaticToneMapping`), `grabberV4L2`; unknown types too;
- CEC (`systemControl.cecControl`) and signal detection options (`systemGrabber`) are removed;
- removed effects map to the kept ones (e.g. "Knight rider" -> "Breath", music color variants -> MULTI COLOR);
- a device type that this build doesn't have becomes the `file` driver (no output), keeping color order and refresh
  time; the copy in `imported/` keeps the original for drivers ported later;
- HyperHDR's factory name "My HyperHDR Config" becomes "My Backbloom Config"; an empty `layout` is left absent;
- `systemGrabber.hdrToneMapping` true becomes `auto`, false `off` (see [Screen capture](#screen-capture-windows));
- `soundEffect.device`, a HyperHDR recording device name, becomes `default` (what this PC plays);
- every section is then repaired against its schema;
- credentials: the admin password hash (salted SHA-512) and API token hashes are imported as `LegacySha512` and keep
  working; `AuthService` replaces them with PBKDF2 on first use. A password still at HyperHDR's or the C++ build's
  factory default isn't imported: this version has no factory password, the user sets one for remote access;
- HyperHDR's `network` access options (`apiAuth`, `localApiAuth`, `localAdminAuth`, internet access, IP whitelist)
  are removed.

Converting is a pure function of the source, so importing the same file again changes nothing.

**First start and data directory.** The data directory defaults to the per-user `Backbloom` folder
(`%LOCALAPPDATA%\Backbloom`, `~/Library/Application Support/Backbloom`, `$XDG_CONFIG_HOME/Backbloom`), or
`backend/.local/data` for a build that runs from the source tree. When `settings.db` has no first-start marker,
`<legacy>/db/hyperhdr.db` is imported (`--legacy-data-dir`, default HyperHDR's folder: `~/.hyperhdr` if it exists,
else `%LOCALAPPDATA%\HyperHDR` / `~/Library/Preferences/HyperHDR` / `~/.config/HyperHDR`; source-tree builds don't look
unless told). A failed import is logged and the app starts with defaults; it isn't retried automatically. Either
way the lights wait for the first-run setup; the imported file is remembered (`imported_from`) for its summary.
`backbloom --import <file> --data-dir <dir>` imports a database explicitly (the current settings go to `backups/` first).

**Backups.** `save-db` returns the C++ export format (`Backbloom_export_format_v1`: `instances` + `settings` rows with
JSON text, per-instance types under instance 0, each controller's `device` under its id); `load-db` also accepts
`HyperHDR_export_format_v17`/`v20`. Credentials are never part of a backup.

## Golden fixtures

`tests/fixtures/<area>/cpp-*.json` were exported from the C++ reference by the Qt tests in `tests/golden/` (at commit
`9f49bd2`): each one ran the real C++ code on deterministic inputs. The C++ code is gone, so they are **frozen** and
can't be regenerated; [tests/fixtures/README.md](tests/fixtures/README.md) says how they were produced and lists the
values deliberately changed since (each changed file has a `deviation` field).

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
| `effects/` `{ name, smoothing*, seed, ledCount, gridSize, latchTime, frames: [{ frame, sleepTime, leds \| color \| image }] }` or `{ ..., blocks: [{ audio, frames }] }` | the 10 kept effects, stepped like `Effect::run`; music effects through `SoundCapture::analyzeSpectrum` | `EffectsGoldenTests` | LED colors, pictures (zlib RGB) and step times match byte for byte; Candle and Full color mood blobs with the MSVC `rand()` sequence after seed 42 |
| `rgbw/` | `RGBW::rgb2rgbw`, `InfiniteColorEngineRgbw` | `RgbwGoldenTests` (Devices.Tests) | every algorithm and the 4 dithering setups match byte for byte |

`settings/owner-hyperhdr-v22.db` is the owner's HyperHDR v22 database, sanitized: admin password, salt and session
token replaced (the password is `fixture-password`), meta uuid replaced, WLED host blanked, microphone and display
names replaced by `default` / `auto`, file vacuumed (no free pages with old content). Everything else (168 LEDs,
color, smoothing, effects, capture, removed-feature records) is as the owner had it.

Tolerance is 0 for every exported fixture: the mapping sums integers and does the same float conversion and divisions
as C++, and the color pipeline builds the same float tables (`powf` of the same C runtime) and runs the same float
operations in the same order (no FMA contraction on either side). Hand-written fixtures (`hand-*.json`) state their own
tolerance.

## Known gaps and next steps

- Effects: an active screen hidden behind a higher source (the startup effect, an effect from the UI) isn't watched,
  so if capture stops meanwhile it goes inactive only 800 ms after it is visible again (HyperHDR kept capturing). Music
  mode follows a new default output within 2 s by polling (no `IMMNotificationClient`); the WASAPI capture runs on
  Windows only (macOS/Linux get no sound, music effects stay dark). Effect arguments (`effect.args`) aren't supported.
- Drivers: RGBW dithering uses the device's `refreshTime` rather than the smoothing clock. The Hue API v2 channel
  identify (flashing a channel of a running stream) isn't ported. Serial USB ids are read from the Windows device path
  (Linux/macOS list the ports without them).
- Screen capture: rotated monitors are captured in scanout orientation (the C++ grabber mis-scaled them); the
  "please wait" image while the default LUT is built is not shown (frames pass without the LUT for ~0.3 s);
  `serverinfo.systemGrabbers.modes` lists only the captured monitor (Engine/Server should list `Monitors`); the
  duplication stays open while `SYSTEMGRABBER` is off; HDR content has no highlight roll-off (clipped at SDR white, as
  in C++). The desktop tests skip when the desktop delivers no frames (monitor off).
- Zones and profiles: zones are smoothed with the visible source's smoothing configuration (a music zone over a smoothed
  screen is smoothed too); while the screen shows, zone effects step with the captured frames (`systemGrabber.fps`,
  catching up so their speed is right); a music zone keeps the sound capture open while the output is held; the power
  limit applies to each zone's colors separately. Effect previews (`effects previews`) show effects on the layout as
  saved, not in zones or profiles.
- Multiple controllers: the UI reads WLED outputs itself (browser fetch); other drivers get their LED count typed in
  (no driver reports it yet). Strip brightness scales the processed (gamma-encoded) values, not linear light. Old
  clients that save `leds` remove a one-controller layout (by design) and can't change a multi-controller one. The
  imported HyperHDR instances' own LED layouts aren't converted (they had their own `leds`); MQTT/Home Assistant
  brightness still applies to all controllers. The `instance` command still reports one instance (controllers aren't
  instances).
- Desktop shell: only Windows has a tray, window and power events; macOS/Linux run headless. The tray menu uses the light system menu style (no dark mode). The
  With `--bind` to a single network address the window reaches the server through that address, so it counts as
  another device and signs in.
  A `--service` process in a console isn't told about a logoff (only console close and shutdown are handled), and
  `--service` can't run as a Windows service (session 0) yet. Brightness from Home Assistant scales its static color
  only; the screen keeps the configured brightness.
