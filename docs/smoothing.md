# Smoothing

Smoothing moves the LEDs towards each new frame's colors over time instead of jumping, so fast cuts and flickering
content look calm. It runs on its own thread at `updateFrequency` ticks per second (the rate the controllers then
receive) and stops sending once a transition has finished (three more frames, then one per second), unless
`continuousOutput` is on.

## What the app offers

HybridRgb ("Natural") is the default and the only style offered outside More options; it was tested on real LEDs
with games, video and music (stiffness 150, damping 26 at 50 Hz). The other five stay available under Colors →
Transitions → More options → Style.

- **One setting: the transition time.** The Colors page's Transition time slider and the Intensity presets write
  `time_ms` together with the spring that reaches 90% of a change in about that time at any update frequency
  (`springFor` in `ui/src/settings/smoothing.ts`: slightly over-damped, 0.35 s gives 150 / 26). A faster spring than
  0.15 s would need a stiffness above the setting's maximum, so with a Natural style the slider's times start at
  0.15 s. Its left end, **Instant**, turns smoothing off (`enable`); moving it to a time turns it on again. Stiffness
  and damping (expert tools) override the spring until the time is changed again.
- **Intensity presets** (Home and Colors → Picture): Calm 0.55 s, Balanced 0.35 s (the default settings), Lively
  0.25 s, Intense 0.15 s, each with its saturation and color brightness (luminance) gain. A preset is shown as chosen
  only when transitions are on and time, spring and gains all match.

## The six styles compared

![The six interpolators after a color step](images/smoothing-compare.png)

Each run: LED 0 as the controller received it (file driver, 50 Hz ticks) after a static color step from dark blue
(0, 0, 40) to orange (255, 140, 0), with the same settings (time 150 ms, stiffness 150, damping 26, y_limit 0.03,
smoothingFactor 0); only the type changes.

| Type | In the app | Time to 90 % | Character |
|---|---|---|---|
| `Stepper` | Linear | 120 ms | covers the distance within about `time_ms`, small differences faster; also used for unknown types |
| `RgbInterpolator` | Fluid | 100 ms | Stepper plus `smoothingFactor` (blends each new target with the previous one) |
| `ExponentialInterpolator` | Classic | 319 ms | exponential approach with time constant `time_ms`, independent of the frame rate |
| `YuvInterpolator` | Fluid, no flashes | 360 ms | linear in Y'CbCr with the brightness change per tick limited by `y_limit`: no hue shift while fading |
| `HybridRgbInterpolator` | Natural (the default) | 361 ms | a damped spring per RGB channel (`stiffness`, `damping`): soft start and landing |
| `HybridInterpolator` | Natural, no flashes | 441 ms | the spring in Y'CbCr with the `y_limit` brightness limit |

All six are monotonic for this step (no overshoot). The spring types start and land softly; Stepper and RGB react
fastest. The spring types ignore `time_ms` itself; the app sets their spring from it (see above).

## Settings (`smoothing`)

| Setting | |
|---|---|
| `enable` | initial state of the `SMOOTHING` component |
| `type` | one of the six above |
| `time_ms` | transition time (Stepper, RGB, YUV) or time constant (Exponential); the app also sets the spring from it |
| `updateFrequency` | ticks per second; interval `round(max(1000 / max(f, 20), 5))` ms |
| `smoothingFactor` | target blending (RGB, YUV, HybridRgb) |
| `stiffness`, `damping` | spring constants (Hybrid, HybridRgb) |
| `y_limit` | maximum brightness change per tick (YUV, Hybrid) |
| `continuousOutput` | keep sending at the full rate after a transition |
| `antiFlickeringFilter` | hold an LED until it changes by about half an 8-bit step |

Effects bring their own smoothing configuration (for example 200 ms at 25 Hz, or direct output for music effects).

## How the springs are integrated

Semi-implicit Euler steps of at most 20 ms, 1 / damping and 1 / sqrt(stiffness) seconds (`SpringMath.MaxStepMs`); a
longer tick is split into equal steps. For the usual springs and 50 Hz ticks that arrive on time this is one step per
tick. With one long step a stiff spring oscillated and diverged (stiffness 300, damping 37 at 20 Hz; stiffness 1000,
damping 100 even at 50 Hz), so the lights flickered.

## Differences from HyperHDR

- HyperHDR's Hybrid and HybridRgb transitions never finished when a target channel was below the minimal backlight
  (black, or the zero channels of a saturated color), so smoothing kept sending at the full rate forever. Backbloom
  finishes them when such a channel rests at the backlight level.
- HyperHDR integrated the springs with one step per tick, whatever its length; Backbloom takes the steps the spring
  allows (see above).

Both are described in [backend/tests/fixtures/README.md](../backend/tests/fixtures/README.md).
