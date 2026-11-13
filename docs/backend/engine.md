# Engine: smoothing, sources and priorities

The engine features between capture and output: smoothing, the priority system that decides which source is visible,
screen activity, black border detection and the system load figures. For the user-facing view of smoothing, see
[docs/smoothing.md](../smoothing.md).

## Contents

- [Smoothing](#smoothing)
- [Sources and priorities](#sources-and-priorities)
- [Screen activity](#screen-activity)
- [Black borders](#black-borders)
- [System load](#system-load)

## Smoothing

A port of HyperHDR's `InfiniteSmoothing` and its six interpolators (`Backbloom.Core/Smoothing`), bit-identical to the
C++ ones (`fixtures/smoothing`, tolerance 0), with two deliberate deviations:

- the Hybrid and HybridRgb transitions finish when a channel's target is below the minimal backlight (HyperHDR kept them
  running, and sending, forever);
- their spring is integrated in steps the spring allows, at most 20 ms (one step per tick diverged for stiff springs or
  long ticks).

Both are described in the [fixtures README](../../backend/tests/fixtures/README.md#deliberate-deviations-from-hyperhdr).
HyperHDR's Stepper and Rgb interpolators differ only in target blending, so `StepperInterpolator` is the
`RgbInterpolator` without it.

| Setting (`smoothing`) | Effect |
|---|---|
| `enable` | initial state of the `SMOOTHING` component (changing the setting switches it) |
| `type` | Stepper, RgbInterpolator, YuvInterpolator, HybridInterpolator, HybridRgbInterpolator, ExponentialInterpolator |
| `time_ms`, `smoothingFactor`, `stiffness`, `damping`, `y_limit` | interpolator parameters, as in HyperHDR |
| `updateFrequency` | ticks per second = frames per second sent to every controller while smoothing runs (interval round(max(1000 / max(f, 20), 5)) ms) |
| `continuousOutput` | off: after a transition three more frames, then one per second |
| `antiFlickeringFilter` | an LED keeps its value until it changes by about half an 8-bit step (also without smoothing, as in HyperHDR) |

### Design

`OutputStage` and `Smoother`:

- The frame loop hands each processed frame to the smoother (one lock, a copy into preallocated buffers) and returns.
  The smoothing thread ticks on absolute deadlines of the injected `IClock` and sends the result to every controller
  and the live preview.
- Without smoothing (disabled, or the visible source's configuration is "direct"), frames go out from the frame loop.
- One smoother feeds all controllers (HyperHDR ran one per instance). The configuration is selected when the visible
  source or the settings change, never per frame.
- On Windows, `WindowsClock` waits with a high-resolution waitable timer (wake-ups within ~0.5 ms, 50.0 ticks/s
  measured) without raising the system timer resolution, which Qt's precise timer (`timeSetEvent`, 1 ms) did
  system-wide.
- Time stamps stay integers (HyperHDR rounded them through `float`, which loses millisecond precision after 4.6 hours of
  uptime).
- After a smoothing restart a static color is drawn again, so the next change still fades (HyperHDR re-ran its LED
  update).

### Measurements

Release build, `PipelineProfile` (an explicit test), the 168-LED reference layout, synthetic source at 60 fps, four file
controllers writing to the null device: 1.25 % of one core without smoothing, 1.72 % with HybridRgb at 50 Hz (see also
[several controllers](layout-and-controllers.md#measurements)). Neither path allocates
(`Pipeline_SteadyStateDoesNotAllocate`, `SmoothingTicks_DoNotAllocate`).

## Sources and priorities

A port of HyperHDR's `Muxer` (`Backbloom.Core/Priorities`) behind `PriorityManager` (Engine: lock, 250 ms timeout timer,
events).

- **Visibility.** The lowest priority number among the active sources is visible, unless one was selected manually
  (`sourceselect`). 255 is "nothing": black, and the controllers are switched off, as HyperHDR did; the `LEDDEVICE`
  switch keeps the user's choice.
- **The screen** registers at `systemControl.systemInstancePriority` (245) while `ALL` and `SYSTEMGRABBER` are on.
- **Static colors.** `color` registers a static color (priority 1..253, optional `duration` ms, origin
  `<origin>@<peer>`). Static colors skip the color settings, as in HyperHDR, except the brightness (`scaleOutput`) and
  the power limit; they are drawn once per change and then smoothed.
- **Clearing.** `clear` takes -1 (all colors and effects) or 1..253; `clearall` = -1.
- **`serverinfo.priorities`** lists the visible source first with `priority`, `componentId`, `origin`, `owner`,
  `active`, `visible`, `duration_ms` and, for colors, `value: { RGB, HSL }`. `priorities_autoselect` and
  `activeLedColor` are as in HyperHDR.
- **Effects** implement `ILightSource` (render linear LED colors on the frame loop while visible) and register with
  `Sources.Register` and `AddSmoothingConfig`.

## Screen activity

`ScreenActivity` follows HyperHDR's `SystemControl` rule: the screen registers inactive, becomes active with its first
frame, and goes inactive after 800 ms without one (lock screen, monitor off, capture errors), so the source below it
shows: the background effect, or nothing.

- With nothing left, the controllers are switched off, but while capture is on only after
  `LightingEngine.NoSourceOffDelay` (10 s; HyperHDR: at once). Until then they stay connected and show black, so a
  display change that stops the capture for a second doesn't switch them off and reconnect them (WLED once failed to
  reconnect for minutes after that). Switching capture or the LEDs off still switches them off at once.
- The frame loop only captures for a visible screen, so while the screen is inactive it probes the frame source every
  500 ms (20 ms budget while an effect shows, so the effect keeps its pace; up to 250 ms while idle). The first frame
  makes it active again.
- A static desktop still delivers frames (DXGI repeats the last image), so it never counts as inactive.
- While a source above the screen shows (the startup effect), an active screen isn't watched.

## Black borders

`BlackBorderProcessor` runs on screen frames while the `BLACKBORDER` component is on (initially
`blackborderdetector.enable`; settings changes apply at once). The detected border is left out of the LED mapping (the
map is rebuilt only when the border changes). Off: no border. Unlike HyperHDR, the border survives a frame size change.

## System load

`performance-update` reports the whole computer's CPU and memory (the UI's "whole computer" tiles) from
`ISystemMetrics`: `WindowsSystemMetrics` (`GetSystemTimes`, `GlobalMemoryStatusEx`; HyperHDR used the PDH processor
counter, the same quantity), else this process's share (`ProcessMetrics`). LED updates/s is what the controllers
actually sent (the smoothing rate while smoothing runs).

For the long-term record of these numbers, see [Performance recording](performance-recording.md).
