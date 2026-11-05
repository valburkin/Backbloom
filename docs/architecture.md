# Architecture

One process (`backbloom.exe`) does everything: capture, color processing, smoothing, LED output, the web server for the
UI and the API, the tray and the window. [backend/README.md](../backend/README.md) describes each subsystem in detail;
this page is the overview.

## Pipeline

```
frame source (DXGI screen capture | effect | static color | synthetic test pattern)
  -> priorities (the lowest active priority is visible; 255 = nothing, LEDs off)
  -> image-to-LED map (LED layout, black border, sparse sampling)  [pictures only]
  -> color processing (temperature, calibration, gamma, gains, backlight, power limit)
  -> zones (parts of strips playing their own effect or color, written into their LEDs)
  -> smoothing (optional, own thread and rate)
  -> anti-flicker filter
  -> strip brightness, then each controller's power limits times the brightness (PowerBudget)
  -> every enabled controller (LED device runner, one thread each) and the UI's live preview
```

HyperHDR ran one complete pipeline per "instance"; Backbloom runs one pipeline that fans out to several controllers.

Zones step their effects on the frame loop without waiting for them; the layout's profiles (the app's presets)
switch the compiled rects and zones while they are on, chosen by hand (saved in the `presets` section) or by a running
program (`ProfileMonitor`, polling `IProcessList`), and a profile's Look puts its own transitions and color gains on top of the saved settings
without changing them (`LookTuning`). Turning a profile on clears the colors and effects clients started. The screen
capture's rate follows the smoothing that applies: at least 60 fps while transitions are fast (`CaptureRateFollower`).

The `LightingGate` (Engine) can hold the output: the controllers stay closed while the first-run setup wasn't
completed or HyperHDR runs (`OtherAppMonitor` behind the `IProcessList` platform interface). Network access is
opt-in: the server listens on loopback until `network.remoteAccess` is turned on, and discovery opens sockets only
while a search runs (see [backend/README.md](../backend/README.md#first-run-hyperhdr-and-network-access)).

## Projects and dependency rules

| Layer | Projects | Contents |
|---|---|---|
| 1 | `Backbloom.Core`, `Backbloom.Settings`, `Backbloom.Discovery`, `Backbloom.Mqtt` | Pure logic (layout, mapping, color, smoothing, priorities, effects, music analysis); settings schemas, SQLite store, HyperHDR import; mDNS/SSDP; the MQTT connection. No references to each other. |
| 2 | `Backbloom.Capture`, `Backbloom.Audio`, `Backbloom.Devices`; `Backbloom.Capture.Windows`, `Backbloom.Audio.Windows` | Frame and sound source abstractions; LED drivers and their lifecycle, the guided setup's Improv and WLED clients; the Windows implementations (TerraFX interop). |
| 3 | `Backbloom.Engine` | Orchestration: frame loop, component switches, sources, controllers, effects, preview feed. |
| 4 | `Backbloom.Server`, `Backbloom.Shell`; `Backbloom.Platform.Windows` | Kestrel JSON-RPC + UI files + MQTT integration; tray menu, window placement, power policy, single instance; Windows tray, WebView2 window, autostart, power and session events, monitor sizes, Wi-Fi (wlanapi) for the guided setup. |
| 5 | `Backbloom.App` | Composition root (the only place that creates services), command line, logging, installer hooks and updates. |

A project may only reference projects of lower layers. Platform code lives only in `*.Windows` projects behind
interfaces defined above them, so macOS and Linux get their own projects later.

## Threading

| Thread | Does |
|---|---|
| Frame loop (above normal) | capture or step the visible effect, map, process, step the zones, hand the frame to the controllers and the preview |
| Smoothing (above normal) | one tick per update interval while smoothing runs; idle otherwise |
| One per controller | open with retries, send the newest frame, refresh, close |
| Sound capture | while a music effect runs: read WASAPI buffers, analyze blocks |
| Thread pool | JSON-RPC requests (in order per connection), event pushes, update checks, the process checks (HyperHDR, profile programs), the performance samples (with the GPU counters), the WLED status reads |
| UI thread (STA) | Win32 message loop: tray, WebView2 window, power and session notifications |
| Log writer | the rolling log file |

The frame loop never waits on a controller or a client: a controller that hasn't sent the previous frame drops it.
Settings changes build new immutable objects and swap them in with one reference write.

## Hot path

The frame loop, the controller submit path and the sound capture loop don't allocate in steady state: buffers are
sized when the LED count or frame size changes, lookup tables are precomputed, drivers send from preallocated packet
buffers over connected sockets, and logging uses `[LoggerMessage]` methods never called per frame. Allocation tests in
Core, Devices, Engine, Capture, Capture.Windows and Audio enforce it. Measured with 168 LEDs at 60 fps: about 1.3 % of
one core, 62 MB working set.

## Behavior references

Behavior ported from HyperHDR is pinned by golden fixtures exported from the C++ code
([backend/tests/fixtures](../backend/tests/fixtures/README.md)); they are frozen now that the C++ code is gone. The
deliberate deviations from HyperHDR are listed there and in [backend/README.md](../backend/README.md).
