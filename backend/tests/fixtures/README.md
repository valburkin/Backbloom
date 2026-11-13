# Golden fixtures

Reference data the test projects compare the .NET backend with: outputs of HyperHDR's C++ code on deterministic
inputs, plus a few hand-written cases. This page explains how the fixtures were produced, their formats, and the
deliberate deviations from HyperHDR they record.

The files are copied next to the test binaries and loaded by `Fixtures.Load` / `Fixtures.All`; every `.json` file of a
folder becomes one test case.

## Contents

- [Frozen fixtures](#frozen-fixtures)
- [How they were produced](#how-they-were-produced)
- [Formats](#formats)
- [Tolerance](#tolerance)
- [Deliberate deviations from HyperHDR](#deliberate-deviations-from-hyperhdr)

## Frozen fixtures

The `cpp-*.json` files were exported from the C++ reference implementation (the HyperHDR fork this backend was ported
from) and are **frozen**: the C++ code was deleted after the port reached parity, so they can no longer be regenerated.
Treat them as the specification of HyperHDR's behavior. Change one only to record a deliberate deviation (see
[below](#deliberate-deviations-from-hyperhdr)), never to make a failing test pass.

`hand-*.json` files are written by hand and state their own tolerance.

## How they were produced

The Qt test programs in `tests/golden/` (`MappingGolden.cpp`, `ColorGolden.cpp`, `BlackBorderGolden.cpp`,
`HdrGolden.cpp`, `SmoothingGolden.cpp`, `EffectsGolden.cpp`, helpers in `GoldenFixture.*` and `TestImages.*`) ran the
real C++ classes on deterministic inputs and wrote these files (`ctest --preset win-release -L golden` with the update
variable of `GoldenFixture.cpp` set; without it, ctest compared its output with the committed files). The RGBW fixtures
in `rgbw/` come from `ColorGolden.cpp` too.

The exporter was added in commit `56d620d`; commit `5f70189` is the last one that contains it and the whole C++ code.
To read or build it:

```powershell
git show 5f70189:tests/golden/ColorGolden.cpp
git worktree add <dir> 5f70189
```

## Formats

Input images are not stored but described by a small spec (`solid`, `hgradient`, `vgradient`, `quadrants`, `checker`,
`random` (xorshift32), `overlay`, `hex`; see `tests/golden/TestImages.h` at `5f70189`) that `Golden/TestImages.cs`
rebuilds with the same integer math. Each case carries an FNV-1a checksum of the pixels, which is checked first. Floats
are written with the shortest text that reads back to the same float. `Golden/GoldenDiff` lists every mismatch (where,
expected, actual, difference), up to 20 per failure.

| Folder | C++ source | C# test | Contents |
|---|---|---|---|
| `layouts/cpp-parsing.json` | `LedString` | `MappingGoldenTests` | parsed areas, grid size |
| `mapping/` `{ leds, tolerance, cases: [{ image, width, height, fnv1a, mappingType, sparseProcessing, horizontalBorder, verticalBorder, expected }] }` | `ImageColorAveraging` | `MappingGoldenTests` | the 168-LED reference layout and 3 others, 8 images, 3 sizes (+512x288, 7x5), both mapping types, sparse, borders |
| `color/` `{ step: process \| calibrate, enabled, colorOrder, adjustment, tolerance, inputs, expected, expectedBytes }` | `InfiniteProcessing`, `LedDevice` rounding | `ColorGoldenTests` | the reference settings, 23 variations, 6 color orders, 4 calibration-only; 185 colors each |
| `blackborder/cpp-detector.json` | `BlackBorderDetector` | `BlackBorderGoldenTests` | 15 pictures x 3 sizes x 3 thresholds x 4 modes |
| `hdr/` | `DefaultLut`, `LutLoader`, `decodeBGRA` | `HdrGoldenTests` (Capture.Tests), `ScalerTests` (GPU) | default table (MD5, grid, grays) and the CPU path match exactly; the GPU LUT path matches exactly without scaling and within 1 level of the mean of the looked-up block colors with scaling (the C++ code point-sampled) |
| `smoothing/` | the 6 interpolators (`Infinite*Interpolator`), injected time line | `SmoothingGoldenTests` | the reference settings with each type plus 4 variations (smoothing factor, y_limit, stiffness/damping, 25 Hz); frames at 60 Hz, ticks at the update interval, completion flag and colors match exactly |
| `effects/` `{ name, smoothing*, seed, ledCount, gridSize, latchTime, frames: [{ frame, sleepTime, leds \| color \| image }] }` or `{ ..., blocks: [{ audio, frames }] }` | the 10 kept effects, stepped like `Effect::run`; music effects through `SoundCapture::analyzeSpectrum` | `EffectsGoldenTests` | LED colors, pictures (zlib RGB) and step times match byte for byte; Candle and Full color mood blobs with the MSVC `rand()` sequence after seed 42 |
| `rgbw/` | `RGBW::rgb2rgbw`, `InfiniteColorEngineRgbw` | `RgbwGoldenTests` (Devices.Tests) | every algorithm and the 4 dithering setups match byte for byte |

The `layouts/ui-multi-controller.*.json` files are not from the C++ code: they check the backend's layout compiler
against the UI's (see [LED controllers and the layout](../../../docs/backend/layout-and-controllers.md#validation-and-compiling)).

### Reference HyperHDR database

`settings/reference-hyperhdr-v22.db` is a real HyperHDR v22 database (the reference setup), sanitized: admin password, salt
and session token replaced (the password is `fixture-password`), meta uuid replaced, WLED host blanked, microphone and
display names replaced by `default` / `auto`, file vacuumed (no free pages with old content). Everything else (168 LEDs,
color, smoothing, effects, capture, removed-feature records) is as it was configured.

## Tolerance

Tolerance is 0 for every exported fixture: the mapping sums integers and does the same float conversion and divisions as
the C++ code, and the color pipeline builds the same float tables (`powf` of the same C runtime) and runs the same float
operations in the same order (no FMA contraction on either side). Hand-written fixtures (`hand-*.json`) state their own
tolerance.

## Deliberate deviations from HyperHDR

Each changed file carries a `"deviation"` field. Only the values named here were recomputed with the .NET code; every
other value is still the C++ output.

1. **Spring smoothing finishes at the minimal backlight** (`smoothing/cpp-hybrid-limit-0.05.json`, ticks from
   t = 1305 ms). The Hybrid and HybridRgb interpolators keep each channel at or above the minimal backlight. HyperHDR
   finished a transition only when every channel equaled its target, so a target below the backlight (black, or the
   zero channels of a saturated color such as pure red) never finished, and smoothing kept sending at its full update
   rate without `continuousOutput` (a constant CPU and network load). Now a still channel held at the edge of its range
   with its target beyond that edge has arrived (`SpringMath.HasArrived`). In the fixture the transition to black
   finishes at t = 1365 ms; once finished, the Hybrid interpolator outputs its RGB targets as HyperHDR did for every
   finished transition (here black). The other smoothing fixtures are unchanged (their transitions to black were still
   running when the recording ended). Covered by `SmootherTests.SpringTransition_BelowTheMinimalBacklight_*`.
2. **Primaries calibration maps each primary to its target** (`color/cpp-calibrate-matrix.json`,
   `cpp-calibrate-matrix-rotation.json`, `cpp-process-calibration-matrix.json`, `cpp-process-combined.json`: `expected`
   and `expectedBytes`). With `classic_config` HyperHDR built a matrix with the targets as columns and then transposed
   it, so pure red became (red.R, green.R, blue.R) instead of the red target, unlike the 3D-LUT mode. Now
   out = R * red + G * green + B * blue, the same result as the LUT for the primaries. Covered by
   `ColorProcessorTests.Calibrate_Matrix_MapsEachPrimaryToItsTarget_LikeTheLut`.
3. **Missing calibration colors are ideal, not black** (no fixture affected). `ColorAdjustment.FromJson` gives a color
   that an adjustment entry leaves out its ideal value (red = 255,0,0 and so on); HyperHDR read it as black, which
   switched that primary or corner off. The settings schema always stores all eight colors, so this only matters for
   hand-made settings and API clients. Covered by `ColorProcessorTests.FromJson_MissingCalibrationColors_*`.
4. **Spring smoothing takes steps the spring allows** (`smoothing/cpp-hybrid-rgb-factor-0.3.json`, every tick: the
   fixture runs at 25 Hz). HyperHDR's Hybrid and HybridRgb interpolators integrate their spring with one semi-implicit
   Euler step per tick. With a long tick or a stiff spring that step oscillates and diverges (stiffness 300, damping 37
   at 20 Hz; stiffness 1000, damping 100 at 50 Hz; the lights flicker between the edges of their range), which ruled
   out the faster Looks at low update frequencies. Now a tick is split into equal steps of at most 20 ms, 1 / damping
   and 1 / sqrt(stiffness) seconds (`SpringMath.MaxStepMs`, `SpringMath.Substeps`), and the Hybrid interpolator's
   brightness limit per tick is shared among them. A tick no longer than one such step (20 ms for the fixtures'
   springs) is integrated exactly as before; the other smoothing fixtures are unchanged. Covered by
   `SmootherTests.StiffSpring_ApproachesWithoutOscillating` and
   `SmootherTests.HybridBrightnessLimit_HoldsPerTick_WhenATickTakesSeveralSteps`.
5. **The brightness applies to static colors** (no fixture affected). HyperHDR switched the color processing off for
   static colors (`COLOR`), so the output scale (Home's brightness) didn't change them. Now a `ColorProcessor` without
   the color settings still applies the output scale and the power limit; the other color settings stay skipped.
   Covered by `ColorProcessorTests.Process_WithoutColorSettings_KeepsBrightnessAndPowerLimit`.
