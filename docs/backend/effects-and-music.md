# Effects and music

The effects (ported and new), how they are previewed and mapped onto strips, how they run as sources, and the music
analysis and sound capture behind the music effects.

## Contents

- [Ported effects](#ported-effects)
- [Music effects new in Backbloom](#music-effects-new-in-backbloom)
- [Previews](#previews)
- [Effects play on every strip](#effects-play-on-every-strip)
- [Music effects need sound capture](#music-effects-need-sound-capture)
- [Smoothing and color processing](#smoothing-and-color-processing)
- [Random numbers](#random-numbers)
- [Running effects](#running-effects)
- [Music analysis](#music-analysis)
- [Sound capture](#sound-capture)
- [Measurements](#measurements)

## Ported effects

The ten effects HyperHDR's C++ code kept (`sources/effects`) live in `Backbloom.Core/Effects` and match
`fixtures/effects` byte for byte (LED colors, pictures, step times):

| Effect | Class (HyperHDR) | Output | Step | Smoothing |
|---|---|---|---|---|
| Rainbow swirl / Rainbow swirl fast | `SwirlEffect` (`Animation_Swirl`) | 80x45 conical rainbow, 1 degree per step | 56 / 19 ms (20 / 7 s per turn) | 200 ms at 25 Hz / effect default |
| Full color mood blobs / Warm mood blobs | `MoodBlobsEffect` (`Animation_MoodBlobs`) | LED colors, 5 rotating blobs | 100 ms | effect default / 200 ms at 25 Hz |
| Breath | `FadeEffect` (`Animation_Fade`) | one color, 50 -> 255 gray and back | 29 ms in, 19 ms out, 12 ms holds | effect default |
| Candle | `CandleEffect` (`Animation_CandleLight`) | LED colors, random flicker | asks 200, runs 100 ms | 500 ms at 20 Hz |
| Music: fullscreen pulse / stereo / quatro / pulse waves (MULTI COLOR) | `Music*Effect` (`Animation4Music_*`) | 80x45 picture from the sound | polled every 17 ms, draws per sound block | direct (no smoothing) |

Every effect has a one-sentence `Description` for the UI and a `DisplayName` for menus ("Music: stereo for LED strip
(MULTI COLOR)" is shown as "Stereo"; names stay the ids stored in settings).

## Music effects new in Backbloom

Five music effects have no HyperHDR counterpart, so there are no fixtures for them; `EffectTests` covers them. They
derive from `MusicAnimation`, which takes each sound result in `Update` and paints on every 17 ms step in `Paint`, so
ripples, scrolling and falling levels move smoothly although results come 21.5 times a second. Most map the picture to
the frame's edge (`EdgePositions`: the way from the middle of the bottom edge up either side to the middle of the top,
each pixel taking its nearest edge point, so LED areas of any depth see the same colors).

| Effect | Class | Picture | Smoothing |
|---|---|---|---|
| Music: spectrum | `MusicSpectrumEffect` | the eight band levels along the edge, bass at the bottom (red) to treble at the top (violet); levels jump up and fall back slowly | direct |
| Music: VU meter | `MusicVuMeterEffect` | the loudness (curved, since it is relative to the recent maximum) fills the edge from the bottom, green to red, with a white peak held 0.7 s, then falling | direct |
| Music: beat ripple | `MusicBeatRippleEffect` | each beat starts a ring in the music's color (saturated) that spreads from the middle over the edges in 0.85 s; a dim glow between beats | direct |
| Music: waterfall | `MusicWaterfallEffect` | the music's color (saturated, as bright as the music is loud) enters at the bottom and takes 1.6 s up both sides | direct |
| Music: bass glow | `MusicBassGlowEffect` | the whole frame in a color going once around the hue circle in two minutes, brighter with the bass | direct |

## Previews

`EffectPreview` (the `effects previews` command) gives the UI's effect cards a few looping seconds of an effect on the
user's own LEDs, 15 frames a second: 6 s for effects, 4 s for music effects.

- **Which LEDs.** Every controller's LEDs as effects map them (`ControllerSetup.AllEffectLeds`), so strips anywhere,
  like one behind the desk and one on the ceiling, show what they would do. Without enabled LEDs, previews use
  `EffectPreview.StandardFrame` (16 LEDs along the top and bottom, 9 per side).
- **Simulated time.** Previews are rendered on simulated time, not on the lights. The effect steps at the times it asks
  for; each LED takes the mean of its area of the picture (or its own color from effects that set LED colors; disabled
  LEDs stay dark; LEDs with the same area are sampled once); effects with their own smoothing are followed roughly.
- **Synthetic music.** Music effects hear `SyntheticMusic`, a two-bar 120 BPM loop (kick, hi-hat, bass, chords and an
  arpeggio, bass-heavy in the first bar and brighter in the second): one loop to learn the levels and one recorded, so
  the preview loops seamlessly.
- **Caching.** The UI asks for each effect as its card comes into view; previews are kept until the layout changes.

The UI draws previews, and the live preview, on the layout as the Layout page does: each LED where it is in the room,
the walls and ceiling folded out around the front wall.

## Effects play on every strip

A strip's role is about the screen, so effects and music have their own mapping (`ControllerConfig.EffectLeds`,
`PipelineConfig.EffectMapping`, `LightingEngine.EffectLayout`):

- Screen edge LEDs keep their area.
- Each run of "Screen average" or area LEDs on a strip goes once around the picture's edge, clockwise from the middle of
  the bottom back to it, as an edge section would (the whole picture's average of a spectrum or a ripple is one flat
  color).
- LEDs of a zone get the rect `auto` would give them (zones pause while music, an effect or a color shows).
- Off and dark LEDs stay off.
- Layouts without a `layout` setting (`leds` only) use `leds` for effects too.

## Music effects need sound capture

Without sound capture a music effect would freeze on its last picture (HyperHDR ran them anyway). Instead:

- `EffectEngine.Run` refuses one while `SoundCapture.CanCapture` is false (`EffectStart.NoSound`). The `effect` command
  answers "Music needs sound input, which is off.", and the tray grays the music entries out.
- Switching capture off ends the running ones (what is below shows again), and a music background effect ends and
  comes back with the capture.
- `serverinfo.sound.available` tells the UI, which turns sound input on (`soundEffect.enable`) when a music effect is
  picked while it is off.

## Smoothing and color processing

"Effect default" is the 200 ms / 25 Hz configuration HyperHDR's engine registered for effects. Equal configurations
are shared (HyperHDR's `addCustomSmoothingConfig`), so the engine registers three: default, Candle, direct. Smoothing
applies only while the `SMOOTHING` component is on.

Pictures go through an image-to-LED map of their own (80x45, no black border detection, as HyperHDR disabled it for
effects); LED colors go through the sRGB table. Then the color settings apply as for the screen.

## Random numbers

"Candle" and the start hue of "Full color mood blobs" called C `rand()`. `MsvcRandom` is the Microsoft CRT generator
(state = state * 214013 + 2531011 mod 2^32, result bits 16..30, `RAND_MAX` 32767), so the golden test seeds it with 42
like the C++ fixture export and gets the same sequence. It sits behind `IEffectRandom`. The engine seeds each effect
from `Random.Shared`; HyperHDR never seeded it, and the CRT state is per thread, so every "Candle" flickered the same
way. The random start hue of "Full color mood blobs" has no visible effect in HyperHDR (it adds whole degrees where only
the fraction counts); it is ported as is.

## Running effects

`EffectEngine` and `EffectSource` port HyperHDR's `EffectEngine` / `Effect::run`.

- An effect is a source at its priority (`componentId` EFFECT, `owner` the effect name), stepped on the frame loop at
  absolute deadlines of the engine clock (its own step time, at most 100 ms as in HyperHDR); it pauses while not
  visible.
- It ends when its source is cleared, replaced (another effect or a color at its priority) or its duration is over (the
  muxer's timeout); a music effect holds the sound capture until then.
- A waiting effect returns at once when another source becomes visible.

| Setting | Behavior (as in HyperHDR) |
|---|---|
| `foregroundEffect` (startup) | at start, priority 0, `duration_ms` (0 or -1: 3000 ms, logged), effect or color (default "Breath", a short white fade; HyperHDR's was "Rainbow swirl fast"); when the LEDs start disabled, it runs if they are switched on within 5 s; later changes of the setting don't run it |
| `backgroundEffect` | the idle effect: priority 254, until cleared (default "Warm mood blobs" when turned on); every change of the setting replaces it; `clear -1` keeps it; not listed in `activeEffects` |

The reference configuration (startup "Rainbow swirl fast" 3000 ms, background off) runs the swirl for 3 s at start,
then the screen shows (it became active during the effect, so the controllers stay on).

## Music analysis

`MusicAnalysis` (Core) is HyperHDR's `SoundCapture::analyzeSpectrum` + `SoundCaptureResult`: blocks of 1024 mono 16-bit
samples at 22,050 Hz, the same fixed-point FFT, eight bands, the loudness and the moving color with its integer
arithmetic, and the "two interpolated steps between blocks" smoothing (`enable_smoothing`). The music fixtures feed
their recorded audio blocks through it.

Additions for the new effects, which leave HyperHDR's results unchanged:

- `MusicStats.Band(i)`: the eight band levels 0..255, each relative to its band's recent maximum;
- `MusicStats.Beats`: a beat counter. A beat is a block whose bass (0-258 Hz) is 1.4 times its average over about the
  last 8 blocks, at most one per 5 blocks (230 ms).

## Sound capture

**`Backbloom.Audio`.** `SoundCapture` applies `soundEffect` (`enable`, `device`, `enable_smoothing`; changes apply at
once, while HyperHDR only read them at the next start) and runs the platform capture while at least one music effect
holds it (HyperHDR's `open`/`close` handles). `AudioBlockWriter` turns any device format (float or 16/24/32-bit PCM, any
rate and channel count) into the analysis blocks: channels averaged, each 22,050 Hz sample the mean of the input over
its time span. Without sound capture a music effect shows black. The logs say when sound is detected or silent (as
HyperHDR did). `enable` defaults to true (HyperHDR: false): the capture only runs while a music effect does, and the
default output needs no microphone permission, so music effects work without setting anything up.

**`Backbloom.Audio.Windows`.** WASAPI, shared mode, event-driven (waits at most 50 ms), the device's mix format.

| `soundEffect.device` | Captures |
|---|---|
| `default` (the schema default) | what the default output device plays (loopback), following Windows when the default output changes (checked every 2 s) |
| `loopback:<endpoint id>` | what that output device plays |
| `input:<endpoint id>` | that recording device (microphone, line in) |
| a device name (HyperHDR stored waveIn names) | the recording device with that name (waveIn names are cut to 31 characters); imports replace such names with `default` |

- Endpoint ids (`{0.0.0.00000000}.{guid}`) survive restarts and renaming, unlike names.
- A loopback stream delivers nothing while nothing plays; that time is fed to the analysis as silence.
- A lost device (unplugged, disabled, format change) is reopened every second.
- Interop is TerraFX, as for screen capture: COM calls without wrappers, so the read loop allocates nothing (NAudio
  would add a dependency with its own allocation per buffer; CsWin32 needs per-API generator configuration).

**`serverinfo.sound`** is `{ active, device, sound_available: [ids], sound_devices: [{ id, name, kind }], running, error }`.
`sound_devices` (names for display), `running` (sound arrives) and `error` (why the device failed, in words for the
failures a user can fix: microphone access blocked in Windows privacy settings, device disconnected or in exclusive
use) are additions; `active` is false while capture is disabled or the device fails. It is pushed as `sound-update`
when the capture starts, stops, fails, recovers, or hears sound or silence. Each capture session reports through its
own observer, so a session being replaced (the device changed) can't mark the new device as failed. The UI's Effects &
Music page shows the names, grouped into outputs and microphones, and the error.

HyperHDR could only capture recording devices, so its `soundEffect.device` is a microphone name because nothing else
was offered. Music on a PC plays through its output, so imports (`LegacyMigrations.UseDefaultSoundDevice`) set it to
`default`. Ids chosen in Backbloom (`loopback:`, `input:`) are kept.

## Measurements

Release build, the 168-LED reference layout, file driver to the null device, no clients, smoothing off as in the
reference configuration: "Rainbow swirl fast" at its full 52.6 steps/s takes 0.62 % of one core; music mode (pulse,
default output in loopback while music played, 21.5 blocks/s) takes 0.16 %.
