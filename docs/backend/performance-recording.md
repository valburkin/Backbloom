# Performance recording and diagnostics

`PerformanceRecorder` (Engine) records how the app performs, so a problem can be looked into afterward (a gaming
session, a user's crash). This page describes what is measured, how controllers report about themselves (WLED
telemetry), the warnings written to the log, and the file format.

## Contents

- [Recording](#recording)
- [Crash reports](#crash-reports)
- [Log warnings](#log-warnings)
- [GPU](#gpu)
- [LED power](#led-power)
- [Controller telemetry (WLED)](#controller-telemetry-wled)
- [Open points](#open-points)
- [File format](#file-format)

## Recording

- When the app starts, the recorder writes a start line; then every 10 s one sample, a JSON object on one line.
- Samples come from counters that already exist: the engine's frame count, the controllers' `FrameStats`,
  `Environment.CpuUsage`, `GC`, `Process`, and the recorder's own `ISystemMetrics` instance (keyed `recorder`, so the
  UI's tiles keep their own interval).
- A timer ticks every 2 s for the CPU peak; the sample is taken on the tick closest to 10 s (ticks come a few ms early
  or late).
- It runs on a timer thread, never on the frame loop. A sample costs a few ms of CPU (one `Process` snapshot), and an
  exception only skips the sample (logged once).
- `PerformanceRecording` (App) appends the lines to `logs/performance-yyyyMMdd.jsonl`. The file is opened and closed
  per line, so it can be read while the app runs. There is a new file per day, and the newest 14 files are kept (the
  last 14 days the app ran): about 4 MB per full day plus 1.3 MB per controller.

## Crash reports

The start line and the last 30 minutes (180 samples) stay in memory as their JSON lines. When the app ends on an
unexpected error, `CrashReport` first appends the `FATAL` entry to the log, naming `crash-yyyyMMdd-HHmmss.jsonl`, then
writes the lines there (the newest 10 kept; a failure there never costs the entry). A crash report can thus send the
error, the log and the performance before it together. The recorder is taken once the app runs, so the crash path
resolves no services. Up to 10 s before the crash are missing (the last sample is the last complete one).

## Log warnings

- A controller starts dropping frames: at least 10 and 5 % of the frames handed to it in one sample. A frame counts as
  dropped only while the controller is ready; while it opens or waits to retry nothing is taken, which isn't falling
  behind. A line follows when it keeps up again (below 1 %).
- Garbage collections paused the app for 100 ms or more in one sample.
- The WLED limiter holds the LEDs at 95 % of its limit or more (until below 80 %).
- A temperature reaches 65 °C at a sensor or 85 °C for the chip (ESP32 chips run at 50-70 °C; WLED's own alarm is at
  95 °C), until 5 °C below.

## GPU

`IGpuMetrics` (`WindowsGpuMetrics`, Platform.Windows) reads the performance counters Task Manager uses (PDH:
`GPU Engine(*)\Utilization Percentage`, `GPU Adapter Memory(*)\Dedicated Usage`, `GPU Process Memory(*)\Dedicated
Usage` / `Shared Usage`) on every 2 s tick:

- the busiest engine of any adapter, for the computer (summed over processes) and for Backbloom (the screen capture),
  time-weighted over the sample plus the peak tick;
- the GPU memory in use.

The query opens on the first tick (about 140 ms, not on the app's start); a reading then takes about 1 ms. The
wildcards are expanded on every collection, so the capture's device, created later, is included. With hundreds of
instances, a reading parses the names in place from one reused buffer and keeps a total per engine between readings,
so it hardly allocates (the app's allocation numbers describe Backbloom, not its measuring). A failure only leaves the
GPU unknown.

## LED power

For overheating questions, `LedDeviceRunner` measures each frame it sends, on the device thread (not the frame loop;
one pass over the colors, no allocation):

- the means of the red, green and blue channels (the share of full brightness the LEDs' PWM draws; the load is their
  mean);
- the brightest single LED;
- how long the driver took to send it.

`FrameStats` sums them and keeps the peaks, which only the recorder takes (through the engine) once per sample.

The estimated current uses the controller's own LED current when it reports one (WLED's "LED current" per output, 255
meaning WS2815 at 12 mA), else WLED's default of 55 mA per LED at full white (WS281x at 5 V). It is what Backbloom asks
for, before the controller's own brightness and power limit. It is left out for lamps (Hue, LIFX, Nanoleaf, WiZ,
Yeelight, Home Assistant, Zigbee2MQTT) and the file driver.

## Controller telemetry (WLED)

Drivers whose controller reports about itself implement `ITelemetryDriver`. The WLED driver was checked against WLED's
`json.cpp`, `cfg.cpp` and `bus_manager.cpp` (v16).

**Power limiter settings.** The driver reads `GET /json/cfg` on connecting, after a restart and every 6th reading
(about once a minute, as it steers the output; see [Power limits](layout-and-controllers.md#power-limits)): the global
limit `hw.led.maxpwr`, each output's limit `hw.led.ins[].maxpwr` and LED current `ins[].ledma`, and "force max
brightness" for realtime data (`if.live.maxbri`). They are interpreted by the controller's version (`info.ver`,
`WledConfig`):

| WLED version | Limiter rules |
|---|---|
| 0.16 and the development branch (`initializeABL`) | a global limit with an output that has an LED current; without a global one, the outputs that have both (then the limit is their sum) |
| 0.15 | only the outputs' own limits (`BusDigital::estimateCurrentAndLimitBri`; an output below its share of the 100 mA kept for the ESP isn't limited); a global limit is ignored |
| 0.14 and older | one LED current `hw.led.ledma`; a limit below 150 mA counts as none |
| unknown | the limiter counts only when 0.15's and the newer rules agree |

**Status readings.** While connected, the driver reads `GET /json/si` (state and info, without the effect lists) every
10 s. It never reads in `Write` and never while switched off; the readings are published only once a connection
succeeded and are cleared on close, which waits for the poll to end, so nothing it sends can come after the switch-off.
It records:

- WLED's current estimate, only while the limiter is in use (`leds.pwr` is then the LEDs plus the controller's own
  80-120 mA; v16 reports those 80-120 mA without the limiter, and `leds.maxpwr` is the global setting whether in use or
  not);
- its brightness (`state.bri`: applied on top of what Backbloom sends unless "force max brightness" is on; a change
  between readings is logged, for example one made in the WLED app);
- on, frame rate (`leds.fps`: LED updates, one per UDP packet above 489 LEDs), LED count;
- realtime state and sender (`live`, `lm`, `lip`: shows another program streaming to it), realtime override (`lor`: 1
  until the realtime data ends, 2 until a restart), connected WLED apps (`ws`);
- chip, Wi-Fi, free memory, uptime;
- a temperature: entries of `sensor` and `u` with "temp" in the name and a degree unit. The Temperature usermod reports
  a missing sensor as `[0, " Sensor Error!"]`, which is skipped; "internal" names the chip's own, and a sensor elsewhere
  is preferred.

**Restarts.** A restart is noticed when the boot time (reading time minus uptime) moves by more than 30 s. While
connected, it is counted, logged as a warning (power or heat), and the realtime state (on, brightness) is sent again,
as WLED forgot it, on every reading until that succeeds (a controller that just booted may not answer yet). Noticed on
connecting, it happened while switched off (for example, the power supply was off) and is only logged. Three failed
reads in a row are logged.

## Open points

- Crash report upload isn't built yet. Controller names and ids and the profile id are text the user typed; decide what
  is sent before uploading the lines.
- Short stutters don't show in 10 s averages; a frame-interval maximum or a count of late frames would be the next
  fields to add.

## File format

Format version 1. Fields are only added; a change of meaning raises `v`. Every line has `v` and `kind`.

### Start line

`kind` is `"start"`.

| Field | Description |
|---|---|
| `time` | local time with offset |
| `version` | app version and build (`0.1.0+1a2b3c4d`) |
| `os`, `runtime`, `cores` | operating system, .NET runtime, logical processors (`app.cpu` is a share of all of them) |
| `pid`, `processStart` | process id and start time: a new start line marks a restart |

### Sample

`kind` is `"sample"`.

| Field | Description |
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
