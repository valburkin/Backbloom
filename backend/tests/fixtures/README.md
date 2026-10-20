# Golden fixtures

Reference data the test projects compare the .NET backend with. They are copied next to the test binaries and loaded
by `Fixtures.Load` / `Fixtures.All`; every `.json` file of a folder becomes one test case.

## Frozen

The `cpp-*.json` files were exported from the C++ reference implementation (the HyperHDR fork this backend was ported
from) and are now **frozen**: the C++ code was deleted after the port reached parity, so they can no longer be
regenerated. Treat them as the specification of HyperHDR's behavior. Change one only to record a deliberate deviation
(see below), never to make a failing test pass.

How they were produced: the Qt test programs in `tests/golden/` (`MappingGolden.cpp`, `ColorGolden.cpp`,
`BlackBorderGolden.cpp`, `HdrGolden.cpp`, `SmoothingGolden.cpp`, `EffectsGolden.cpp`, helpers in `GoldenFixture.*` and
`TestImages.*`) ran the real C++ classes on deterministic inputs and wrote these files
(`OVERGLOW_UPDATE_FIXTURES=1 ctest --preset win-release -L golden`; without the variable ctest compared its output with
the committed files; the RGBW fixtures in `rgbw/` come from `ColorGolden.cpp` too). The exporter was added in commit
`cd0932d`; commit `9f49bd2` is the last one that contains it and the whole C++ code
(`git show 9f49bd2:tests/golden/ColorGolden.cpp`, or `git worktree add <dir> 9f49bd2` to build it).

`hand-*.json` files are written by hand and state their own tolerance. `settings/owner-hyperhdr-v22.db` is the owner's
HyperHDR v22 database, sanitized (see `backend/README.md`).

| Folder | C++ source | C# test |
|---|---|---|
| `layouts/` | `LedString` | `MappingGoldenTests` |
| `mapping/` | `ImageColorAveraging` | `MappingGoldenTests` |
| `color/` | `InfiniteProcessing`, `LedDevice` rounding | `ColorGoldenTests` |
| `blackborder/` | `BlackBorderDetector` | `BlackBorderGoldenTests` |
| `hdr/` | `DefaultLut`, `LutLoader`, `decodeBGRA` | `HdrGoldenTests`, `ScalerTests` |
| `smoothing/` | `Infinite*Interpolator` | `SmoothingGoldenTests` |
| `effects/` | the 10 kept effects, `SoundCapture::analyzeSpectrum` | `EffectsGoldenTests` |
| `rgbw/` | `RGBW::rgb2rgbw`, `InfiniteColorEngineRgbw` | `RgbwGoldenTests` |

The formats are described in `backend/README.md` ("Golden fixtures").

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
