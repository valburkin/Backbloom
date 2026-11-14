# Threading and the hot path

Which thread does what, how settings changes reach the frame loop without locks on the hot path, and the rules that
keep the per-frame path free of allocations. The [architecture overview](../architecture.md) has the short version.

## Contents

- [Threading model](#threading-model)
- [Hot-path rules](#hot-path-rules)
- [Measurements](#measurements)
- [Frame pacing](#frame-pacing)

## Threading model

| Thread | Owner | Does |
|---|---|---|
| Frame loop (1, above-normal priority) | `LightingPipeline` | capture (or step the visible effect) -> map -> slow down the "Screen average" LEDs -> process -> zones (step their effects without waiting, write their colors) -> hand frames to controllers and the preview feed |
| Sound capture (1 while a music effect runs, above-normal) | `WasapiSession` | read the device's buffers, convert them into blocks, analyze each block |
| Smoothing (1, above-normal priority) | `OutputStage` | while smoothing runs: one tick per update interval (interpolate, anti-flicker filter, hand the frame to the controllers and the preview); idle otherwise |
| One per controller | `LedDeviceRunner` | open (with retries), send the newest frame (its slice of the LEDs), refresh, blink, close |
| Thread pool | ASP.NET Core, `OtherAppMonitor`, `ProfileMonitor`, `PerformanceRecorder` | JSON-RPC requests (in order per connection), event pushes, LED stream timers, the HyperHDR process check, (while the layout has profiles) the profile programs check every 2 s, and the performance samples (CPU every 2 s, a sample every 10 s) |
| UI (1, STA, Windows) | `UiThread` | Win32 message loop: hidden message window (power/session notifications, tray callbacks), tray menu, WebView2 window |
| Log file (1, normal: idle almost always, and starved below it when the PC is busy) | `RollingFileLoggerProvider` | writes queued log lines; closes the file when it stops |

- **The frame loop never waits on a controller or a client.** `LedDeviceRunner.Submit` copies the colors into a
  preallocated buffer; if the device thread hasn't sent the previous frame yet, that frame is dropped (counted in the
  stats), never queued.
- **Settings changes swap immutable objects.** Changes arrive on request threads. They build new immutable objects
  (`PipelineConfig`: LED map options + `ColorProcessor`) and swap them in with one reference write; the frame loop
  picks them up on its next frame.
- **Controllers are recreated only when needed.** When `layout`, `device` or `leds` change, a controller is recreated
  only if its driver settings, name or LED count changed (the old one is switched off first: black frame, power off);
  the others keep running at their new position.
- **Events are serialized per session.** Events (`*-update`) are raised on the thread that caused the change; each
  WebSocket session serializes them into its own outbound queue, drained by one send loop.

## Hot-path rules

The frame loop, `LedDeviceRunner.Submit` and the sound capture loop must not allocate in steady state.
`GC.GetAllocatedBytesForCurrentThread` tests enforce it in Core, Devices, Engine, Capture, Capture.Windows and Audio.
Core's and Engine's zone tests measure on a dedicated thread after a full collection, in a test collection that runs
without parallel tests, because the counter of a test-runner thread occasionally grew by 4-6 KB under full-suite load
without any allocation by the measured code.

- Buffers are sized when the LED count or frame size changes, not per frame.
- `ImageToLedMap` precomputes pixel offsets per LED into flat arrays.
- `ColorProcessor` precomputes its lookup tables and is immutable.
- `BlackBorderDetector` samples a few lines of the frame in place; `BlackBorderProcessor` only keeps counters.
- Zones keep their LED indices, linear colors and a scratch buffer for the color pass; their effects step and map like
  the visible one (`ZoneTests` measures the screen path with and without smoothing and the idle path).
- Effects step on preallocated state (colors rotate in place, the canvas and its angle table are kept). The music
  analysis and `AudioBlockWriter` work in fixed buffers, and the WASAPI loop hands the device's buffer to the writer as
  a span (`WasapiSession` measures its own allocations; a live loopback test asserts 0).
- Logging goes only through `[LoggerMessage]` methods and never per frame.
- Drivers send with preallocated packet buffers over connected UDP sockets (`Socket.Send(ReadOnlySpan<byte>)`). The
  streaming drivers (WLED, DDP, E1.31, Art-Net, Adalight, Skydimo, SP616E, SyncLight, Nanoleaf, LIFX, Hue
  Entertainment, Govee) and the RGBW encoders are allocation-free per frame. The JSON/REST lamp drivers (WiZ, Yeelight,
  Home Assistant, Zigbee2MQTT, Hue REST) build small strings per frame on their device thread, at most a few times a
  second.

## Measurements

With a 168-LED reference layout, the synthetic source at 60 fps (512x288 frames, 155,684 sampled pixels per frame) and
the file driver writing to the null device (Release build, 24 logical cores): about 1.3 % of one core without clients
and 2.1 % with the UI's live preview connected; working set about 62 MB (22 MB private).

Per-subsystem measurements are on the subsystem pages: [screen capture](screen-capture.md#measurements),
[smoothing](engine.md#smoothing), [effects and music](effects-and-music.md#measurements) and
[several controllers](layout-and-controllers.md#measurements).

## Frame pacing

The synthetic source paces itself by absolute deadlines because the default Windows timer resolution is 15.6 ms; the
average rate is exact, and single frames jitter by up to one timer tick. Screen capture uses the same pacer for its
`fps` limit and then takes the newest desktop image DXGI has.
