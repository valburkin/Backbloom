# Overglow backend (.NET)

The C# / .NET 10 port of the Overglow backend. The C++ code in `sources/` and `include/` is the reference
implementation: behavior is ported from it file by file and compared with golden fixtures exported from it. It is
deleted once this backend reaches parity.

Status: **port step 2** (golden fixtures). The React UI in `ui/` connects to it, a synthetic frame source drives the
LEDs through the real mapping and color pipeline, and the WLED, DDP and file drivers run on their own device threads.
LED layout parsing, image to LED mapping, color processing and black border detection match fixtures exported from the
C++ reference exactly.

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
| `--data-dir` | `backend/.local/data` | Settings (`settings.json`), credentials (`auth.json`, hashes only), instance id. Never the user profile. |
| `--ui-dir` | `ui/dist` of the source tree | Built React UI to serve. |
| `--fps` | `60` | Rate of the synthetic test source. |

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
  Overglow.Capture    frame source abstraction, frame pacer, synthetic test source
  Overglow.Devices    LED device lifecycle (LedDeviceRunner) + drivers: wled, ddp, file
  Overglow.Settings   schema-validated settings store (JSON file), credential store
  Overglow.Engine     orchestration: frame loop, component switches, controllers, live preview feed
  Overglow.Server     Kestrel: JSON-RPC over WebSocket + POST /json-rpc, auth, UI files
  Overglow.App        host: composition root (DI), configuration, logging (exe: overglow)
tests/
  Overglow.*.Tests    xUnit v3; fixtures/ holds golden fixtures shared by the test projects
```

Dependency rules (a project may only use the ones above it in this list):

1. `Core`, `Settings` (no project references)
2. `Capture`, `Devices` (Core)
3. `Engine` (Core, Capture, Devices, Settings)
4. `Server` (Engine and below)
5. `App` (everything; the only place that creates services)

`Overglow.Engine` is an addition to the plan's layout: the API layer needs one orchestrator to talk to
(component switches, live colors, identify), and it can't live in `App` without the server depending on the host.

Platform code (Windows capture, audio, tray) will live only in `*.Windows` / `*.MacOS` / `*.Linux` projects behind
interfaces defined in the projects above.

## Threading model

| Thread | Owner | Does |
|---|---|---|
| Frame loop (1, above-normal priority) | `LightingPipeline` | capture -> map -> process -> hand frames to controllers and the preview feed |
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
tests enforce it in Core, Devices and Engine):

- buffers are sized when the LED count or frame size changes, not per frame;
- `ImageToLedMap` precomputes pixel offsets per LED into flat arrays;
- `ColorProcessor` precomputes its lookup tables and is immutable;
- `BlackBorderDetector` samples a few lines of the frame in place; `BlackBorderProcessor` only keeps counters;
- logging only through `[LoggerMessage]` methods and never per frame;
- drivers send with preallocated packet buffers over connected UDP sockets (`Socket.Send(ReadOnlySpan<byte>)`).

Measured with the owner's 168-LED layout, the synthetic source at 60 fps (512x288 frames, 155,684 sampled pixels per
frame) and the file driver writing to the null device (Release build, 24 logical cores): about 1.3 % of one core
without clients and 2.1 % with the UI's live preview connected; working set about 62 MB (22 MB private).

The synthetic source paces itself by absolute deadlines because the default Windows timer resolution is 15.6 ms;
the average rate is exact, single frames jitter by up to one timer tick. Real capture paces itself (DXGI).

## JSON-RPC protocol

Compatible with the C++ backend for what the UI uses (`ui/src/api/rpcApi.ts`): requests
`{command, subcommand?, tan, ...}`, replies `{command, tan, success, info?, error?}`, events `{command: "*-update", data}`.

| Command | Status |
|---|---|
| `authorize` tokenRequired, adminRequired, newPasswordRequired, login (password / token), logout, newPassword, createToken, getTokenList, deleteToken | done |
| `sysinfo`, `serverinfo` (+ subscriptions), `performance-counters` | done |
| `config` getschema, getconfig, setconfig | done (schema covers general, logger, device, color, leds, layout, network) |
| `componentstate` ALL, SYSTEMGRABBER, LEDDEVICE | done (SMOOTHING, BLACKBORDER come with those features) |
| `adjustment`, `ledcolors` ledstream-start/stop, `leddevice` identify/discover/getProperties, `logging`, `instance` switchTo | done (discover returns no devices until discovery is ported) |
| `color`, `effect`, `clear`, `sourceselect`, `save-db`, `load-db`, imagestream | not yet ("Command not implemented") |

Deliberate differences from the C++ backend (its bug backlog):

1. `setconfig` rejects invalid values with `errors: [{path, message}]` and stores nothing; it never resets values to
   defaults. Unknown sections are rejected too.
2. `adjustment` is stored in `color.channelAdjustment` (so its `settings-update` is true); it needs admin rights.
3. `serverinfo` always returns the info, also when subscribing to `performance-update`.
4. `logging start` replies first; the history follows as a `logging-update` event without the request's tan.
5. Settings can be saved while the lights are off.
6. (Backup restore is not implemented yet.)
7. `layout` has no default: `getconfig` omits it until a client saves one.

Also: `logout` returns the connection to the rights it had before signing in, and the identify blink is applied
before the controller's color order (so it is really red, green, blue on GRB strips).

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
| `hdr/` | `DefaultLut`, `LutLoader`, `decodeBGRA` | `HdrGoldenTests` (smoke) | port step 3 |
| `smoothing/` | the 6 interpolators, injected time line | `SmoothingGoldenTests` (smoke) | port step 5 |
| `effects/` | the 10 kept effects | `EffectsGoldenTests` (smoke) | port step 5 |
| `rgbw/` | `RGBW::rgb2rgbw`, `InfiniteColorEngineRgbw` | `RgbwGoldenTests` (smoke) | port step 6 |

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
- Black borders: `BlackBorderProcessor` is ported but not wired into the pipeline yet (Engine), nor the `BLACKBORDER`
  component switch.
- Windows capture + HDR tone mapping, smoothing, effects, the remaining drivers and discovery, SQLite settings with
  HyperHDR database import.
- Multiple controllers: `DeviceManager` already fans out to a list; the settings still describe one `device`.
