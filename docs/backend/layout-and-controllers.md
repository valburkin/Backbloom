# LED controllers and the layout

How one capture and one mapping pass drive many LED controllers from a room layout: the `layout` data model and its
validation, zones, profiles (the app's presets) and Looks, how the store keeps related settings consistent, the engine's
per-controller slices, power limits, the protocol additions and the UI.

Users think in strips, and a strip is wired to an output of a controller. Backbloom runs one capture, one mapping pass
and many controllers on one clock. There are no per-controller pipelines (HyperHDR's "instances").

> **Terminology.** The code and the protocol say *controller* and *profile*; the UI says *device* and *preset* (see
> [docs/ui-guidelines.md](../ui-guidelines.md#words)).

## Contents

- [Data model](#data-model)
- [Validation and compiling](#validation-and-compiling)
- [Zones](#zones)
- [Profiles (the app's presets)](#profiles-the-apps-presets)
- [Looks](#looks)
- [Automatic capture rate](#automatic-capture-rate)
- [Consistency with other sections](#consistency-with-other-sections)
- [Configurations without a layout](#configurations-without-a-layout)
- [Engine](#engine)
- [Power limits](#power-limits)
- [Protocol additions](#protocol-additions)
- [UI](#ui)
- [Measurements](#measurements)

## Data model

The `layout` setting (the UI's `LayoutSpec`, `ui/src/layout/types.ts`) is the source of truth:

```text
{ version: 1,
  room: { width, depth, height, screen: { x, y, width, height }, furniture },
  controllers: [{ id, name, enabled?, kind?, host?, outputs: [{ index, start, length, pin? }],
                  device: { type, colorOrder, refreshTime, ...driver settings } }],
  strips: [{ id, name, controllerId, output, ledCount, path: [{ surface, x, y, at?, next? }],
             dark?: [{ start, count }], depth?, blend?, brightness? }] }
next: { kind: edge, from, to } | { kind: whole } | { kind: area, area } | { kind: none }
    | { kind: effect, effect } | { kind: color, color: [r, g, b] } | { kind: gap }
profiles?: [{ id, name, apps: [executable names], sections: { <strip id>: [section | null per section] },
              look?: { time_ms, stiffness, damping, saturationGain, luminanceGain, backlightThreshold } | null }]
```

**Room and strips.** Strips are placed in a schematic room (meters). The screen hangs on the front wall, and a strip is
a path of two or more points on the walls (`front`, `left`, `right`, `back`), the `ceiling` or the `floor`, each in that
surface's own coordinates. The points split the strip into sections; a point's `next` is the section to the following
point (the last point's is ignored).

**LED positions.** LED k sits at position k + 0.5 along the path. Points matched to the real lights carry their LED
position `at`; the others are spaced by length. `gap` sections (no LEDs: a cut corner, a cable) count no length, so the
LEDs spread over the other sections.

**Section kinds:**

| Kind | LEDs show |
|---|---|
| `edge` | the stretch of the screen's outline from `from` to `to` (outline positions 0..4 clockwise from the top-left corner, one per edge, wrapping around). Its LEDs spread evenly over the stretch, each `depth` (default 0.1) screen heights into the picture and as wide along the edge as the LED spacing there times `blend` (default 1.2). |
| no `next` (auto) | the edge stretch the section faces: both ends seen straight on against the front wall (depth dropped), each at the nearest point of the outline, the short way round |
| `whole` | the whole picture, averaged once for all of them and slowed down on the frame loop by `ScreenAverageFilter` before the color settings: an exponential filter with a 500 ms time constant, because a whole-screen average is a pale color whose hue swings with small changes in the picture and the saturation gain makes the swings visible |
| `area` | one area of the picture |
| `none` | nothing: the LEDs stay off (as do LEDs in `dark` ranges) |
| `effect`, `color` | something of their own (a [zone](#zones)) while the user's layout shows (the screen, or nothing else); music, an effect or a color the user picks for every light plays on them too |
| `gap` | no LEDs |

## Validation and compiling

`Backbloom.Settings/Layout`:

- `LayoutSpecification.Normalize` validates the layout (rejecting with `layout.<path>` errors, like every setting) and
  fills in each controller's `device`.
- `Compile` turns it into one sampling area per LED per controller with the same math as the UI's
  `ui/src/layout/room.ts` and `compile.ts`. Both are checked against
  `tests/fixtures/layouts/ui-multi-controller.*.json`; the C# output matches bit for bit, and the test allows one step
  of the 4-decimal precision.
- `ControllerSetup` is what the engine runs. Compiling runs only when the layout changes, never per frame.

**Rules** (the errors of the UI's `validate.ts`; its warnings aren't enforced):

- a room 0.5 to 50 m each way, a screen of positive size on the front wall, `furniture` desk, tv or none;
- at least one controller; unique controller and strip ids;
- a controller's outputs have unique indexes, at least 1 LED each, and follow each other from LED 0 without gaps or
  overlaps, so its LED count is the sum of its outputs (at most 10,000);
- a strip has a whole `ledCount` of at least 1, and a path inside the room with a length above 0 outside its gaps,
  whose `at` positions lie within the strip and don't go backwards;
- sections are of a known kind: `edge` with numeric `from` and `to`; `area` with numeric bounds, minimum not past
  maximum (an area reaching outside the picture is cut to fit); `effect` with a non-empty name, which needn't exist in
  the catalog; `color` with three whole numbers 0..255;
- `dark` ranges lie within the strip, `depth` is in (0, 1], `blend` above 0 and `brightness` 0..1;
- a strip names an existing controller and output (one strip per output) and has at most as many LEDs as the output
  (fewer: the rest stays dark, and the UI warns);
- each `device` is validated against its driver's schema (defaults filled in);
- `profiles`, when present, is a list of profiles with unique non-empty ids, a name, `apps` as a list of non-empty
  program names and `sections` as an object of lists, whose entries are null (auto) or valid sections other than
  `gap`. A profile's list for a strip that isn't there, or whose length isn't the strip's section count, is only a
  warning in the UI and is ignored when the profile is applied, as are profiles without programs.

Properties the backend doesn't use (`kind`, `host`, `pin`, `name`, and the `seat`, `show` and `area` of an earlier,
never released room model) are kept.

## Zones

The LEDs of `effect` and `color` sections are disabled in the compiled `leds` (the screen's mapping skips them) and form
zones (`ControllerSetup.Zones`, `compileZones`): one per distinct role (the same effect, or the same color) across
strips and controllers. Each LED keeps its index among all controllers' LEDs and the rect `auto` would give it
(`CompileStrip(strip, room, zone: true)`), so an effect's picture flows across the zone as if it were the screen. Dark
LEDs and LEDs past their output aren't in a zone. This is checked against `ui-multi-controller.zones.json` (indices
exactly, rects within 1e-4).

The engine plays zones over the screen's frames, over the background (idle) effect (it fills only the other LEDs) and
while nothing shows (`ZoneLayer`, `LightingPipeline.ShowsLayout`). While music, an effect or a color shows (another
client's, or the UI's color while its picker is dragged; the UI stores what plays in the sections, so an effect on
every strip is one zone), zones pause and their LEDs play it, mapped with `PipelineConfig.EffectMapping` (where `auto`
would put them).

- An `effect` zone runs its own `EffectSource`, mapped with the zone's rects (with the user's mapping type) or, for
  effects that set LED colors, with one color per zone LED. A music effect opens the sound capture like a visible one
  and stays dark without sound. An effect name that isn't in the catalog leaves the zone dark (logged once).
- A `color` zone shows its color.
- Effect zones go through the color settings like effects, color zones past them like static colors. Then they are
  written into their LEDs of the frame handed to `OutputStage` (smoothing, if on, and strip brightness apply to them
  too).
- Zone effects step without waiting (`EffectSource.TryStep`, catching up on steps missed since the last call so they
  keep their speed):
  - with every frame of the visible source;
  - while a visible effect waits for its next step (it returns early for a zone step, `ILightSource.TryRender`'s
    `wakeAtMs`);
  - while nothing new comes (a static color, no source, an inactive screen): the frame loop wakes for the zones' next
    step (in slices of 20 ms, so a new source still shows at once) and sends the last frame again with the zones' new
    colors.

  A screen capture isn't cut short for them (the frame pacer would skip a frame), so while the screen shows they step
  with its frames.
- While zones exist, the controllers stay on without a source (the other LEDs are black); zone effects pause while the
  output is off or held.
- A layout change rebuilds the zones; an effect whose zone is still there keeps running (same instance).

## Profiles (the app's presets)

`layout.profiles` give the strips' sections other roles while a profile is on, chosen by hand or while one of its
programs runs (a game, a video player).

**Applying a profile.** `LayoutSpecification.ApplyProfile` (`applyProfile` / `stripInProfile` in
`ui/src/layout/profiles.ts`) puts a profile's sections onto each strip it lists with a list as long as the strip's
sections (null: auto; gaps stay), and `Compile(spec, profileId)` compiles that (checked against
`ui-multi-controller.witcher.*.json`).

**Choosing by program.** `ProfileMonitor` (Engine) asks `IProcessList.FindRunning` for each profile's programs, in list
order, at start and every 2 s while the layout has profiles. The first profile one of whose programs runs (names
compared case-insensitively) is applied with `LightingEngine.ApplyProfile`, which compiles the stored layout for it and
swaps rects and zones without saving anything. Controllers whose driver settings and LED count didn't change keep
running (a profile can't change them).

**Choosing by hand.** `profile select` turns one on by hand until `profile dismiss`, another `select`, or `select` back
to automatic; removing that profile from the layout goes back to automatic. Later layout saves are compiled for the
active profile.

**Saving the choice.** The profile chosen by hand is saved in the `presets` settings section (`{ selected?: string }`,
schema `Schemas/presets.json`, default `{}`; `ProfileMonitor.SectionName`), so nothing the user picks is temporary:

- `Start` restores it while the layout has that profile and clears it otherwise;
- `select` sets it; `select` null, `dismiss` of the chosen profile and its removal from the layout clear it;
- `ProfileMonitor.Save` writes it only when it differs from the stored value, without the monitor's lock (the store
  raises `Changed` on the saving thread, and its handlers take the engine's lock: the order stays monitor -> engine ->
  capture rate follower -> store); a separate save lock keeps concurrent saves in order, each saving the choice as it
  is then;
- the monitor also follows a `presets` change written by someone else (`setconfig`, `load-db`) like a `select`, and
  ignores the change events of its own saves (recognized by the saving thread), so a save never loops;
- dismissals stay in memory.

**Dismissing.** `profile dismiss` (the UI's Everyday, which dismisses until no profile is on) turns the active profile
off. One chosen by hand goes back to automatic, and the profile goes on a dismissed list while any of its programs runs,
so it doesn't come back by its program (the first profile in list order whose program runs and that isn't dismissed is
chosen instead). Each check drops the dismissals whose programs all quit (the next run turns the profile on again);
`select` of a dismissed profile, removing it from the layout and a restart end its dismissal too. The programs are
polled while the layout has profiles and either none is chosen by hand or one is dismissed.

**State and events.** `ProfileMonitor.State` is `{ Profile, Dismissed }` (the first dismissed profile in list order);
`Changed` is raised with it after each change, outside the monitor's lock, with the state as it is then.

**Clearing client sources.** When the active profile's id changes to another, non-null one, the monitor calls
`LightingEngine.ClearClientSources` (`Muxer.ClearClients`, not in HyperHDR: every color, picture and effect at
priorities 1 to 253; the screen, the startup effect at 0 and the background effect at 254 stay), so the profile's own
strips show and the last choice wins.

## Looks

A profile's optional `look` (`ProfileLook`: `time_ms`, `stiffness`, `damping` as whole numbers in the `smoothing`
schema's ranges, `saturationGain`, `luminanceGain` 0..10 and `backlightThreshold` 0..1) is what the app's Look presets
write into the saved `smoothing` and `color.channelAdjustment[0]`, kept with the profile.

While the profile is on, `LightingEngine` (`UpdateLook` after each profile switch and layout change) builds the color
processor and the smoothing from `LookTuning` applied to the saved sections: smoothing on with the Look's time and
spring, the first channel adjustment's gains and minimum glow replaced, its brightness (`scaleOutput`) and everything
else kept. Nothing is saved, the `SMOOTHING` component follows the applied setting, and saved changes made meanwhile
still apply underneath. A profile without a Look keeps the saved one.

## Automatic capture rate

`LightingEngine.SmoothingApplied` is the smoothing that applies now (saved, or with the Look). `SmoothingAppliedChanged`
(raised under the engine's lock, in order) tells `CaptureRateFollower` (App), which reconfigures the screen capture with
`LookTuning.CaptureFor`: at least 60 fps while smoothing is on with a transition under 300 ms, else
`systemGrabber.fps` (a faster saved rate stays). It reconfigures only when the resulting `CaptureSettings` differ,
since each new configuration restarts the capture; until the engine exists, it uses the saved smoothing. The follower
lives in App because the engine depends on the capture source.

## Consistency with other sections

The store keeps the other sections consistent with the layout, in the same transaction:

| Saved | Effect |
|---|---|
| `layout` | `leds` = every controller's LEDs one after the other; `device` = the first controller's; the `controllers` table mirrors the layout (ids 0..N-1, name, enabled, device; backups and HyperHDR-format exports keep working). A controller sent without `device` keeps the one it had (by id), else (first save after an import) the stored controller at its position, else the default (`file`). `leds` sent along is replaced by the derived one. |
| `device` (old clients) | changes the first controller of the layout |
| `leds` (old clients, HyperHDR's LED editor) | unchanged: nothing happens. Different, with a one-controller layout: the layout is removed and `leds` + `device` describe the LEDs again; with several controllers: rejected ("save the layout instead") |
| neither (a HyperHDR import, no layout saved yet) | the legacy controller: `device` driving `leds`, id `controller-1` |

## Configurations without a layout

Nothing is rewritten: a configuration without `layout` (every HyperHDR import) runs as one controller from `leds` +
`device`. The first layout saved by the UI takes over (the UI builds it from those two and keeps the device).

- A stored layout without devices (one controller) gets the stored controller's device on load.
- An invalid stored layout is ignored with a warning (the LEDs follow `leds` + `device`). That includes layouts in an
  earlier, never released format (strips made of screen-edge `segments`, no room): there is no conversion, and the UI
  starts a new room layout from `leds`.
- Imported HyperHDR instances beyond the first are stored as controllers but get LEDs only when a layout uses them.

## Engine

- **One pass for all controllers.** The mapping runs once per frame over the union of all controllers' LEDs (the
  pipeline's `LedLayout` is `leds`), so N controllers cost one `ImageToLedMap` pass, one color pass and one smoother,
  exactly like one controller with that many LEDs; per-controller rect sets would only add work.
- **Slices.** `DeviceManager` gives controller N the slice that follows controllers 0..N-1 (`Submit` hands each runner a
  `ReadOnlySpan` slice; no allocation). Every controller gets every frame from the same thread, and each runner sends on
  its own thread, so a slow, unreachable or retrying controller only drops its own frames (`MultiControllerTests`: a
  driver whose writes take 300 ms doesn't delay the others or `Submit`).
- **Strip brightness** is a per-LED gain applied after smoothing and the anti-flicker filter (`OutputStage`; null when
  every strip is at 100 % and no LED is off), so the preview shows it too. LEDs that are off (disabled in the layout, of
  no group and no zone: "off" sections, dark LEDs, outputs no strip uses) get the gain 0, so the color settings'
  minimal backlight and the smoothing, which keep every LED at least that bright, don't leave them glowing.
- **Latency.** Each runner measures the time from `Submit` until the driver had written the frame (`FrameStats`
  latency).

## Power limits

`PowerBudget` runs in `DeviceManager.Submit`, after the strip brightness and before the preview.

**Why.** WLED's limiter cuts everything above its limit, so with a power supply smaller than full white needs, the top
of Home's brightness slider would change nothing on bright scenes.

**How.** Each group of LEDs that a controller's limiter holds to a budget (`ControllerTelemetry.PowerGroups`) is held at
93 % of its budget times the brightness's share of the output, but never below the current the minimum glow needs.

- **Groups** for WLED (`WledConfig.ReadGroups`), by version: 0.14 every output under the global limit; 0.15 each
  limited output on its own; 0.16 the outputs with an LED current under a global limit, else as 0.15.
- **Budget** = limit - the ESP's 120 mA (the most WLED reserves; per output: its share over all outputs on 0.15, over
  the limited ones on 0.16) - 1 mA per LED at rest.
- **Brightness share** (`ColorProcessor.OutputShare`): the sRGB-encoded scale, for the screen and effects with the user
  gamma and a color brightness gain above 1, at most 1; handed over with each frame (`OutputStage.Submit`).
- **An LED's estimate** is WLED's: its LED current at full white times the mean of its channels (the brightest channel
  for WS2815) times the controller's own brightness (unless "force max brightness").

At 100 % the lights give what the supply allows (dark scenes their full light); every lower value dims bright scenes
too. 93 % keeps WLED's limiter out and its reading under the diagnostics' 95 % warning.

**Left to WLED.** LEDs outside a group (outputs WLED doesn't limit, LEDs beyond Backbloom's) aren't touched; a group
with a white channel or an unknown bus type, a used-up budget or an unknown firmware version is left to WLED. WLED's
limiter stays the safety net, and steps in where its view differs from Backbloom's: a frame of more than 489 LEDs shown
packet by packet, an LED map or segment grouping on the controller, LEDs it has beyond Backbloom's. Controllers without
power groups aren't limited.

## Protocol additions

Old single-controller clients keep working.

| Command or field | Behavior |
|---|---|
| `config getconfig` / `setconfig` | `layout` with every controller's `device`; `leds` (all LEDs) and `device` (first controller) as [above](#consistency-with-other-sections) |
| `serverinfo.controllers`, event `controllers-update` | `[{ id, name, type, enabled, running, state, status, start, ledCount, strips: [{ id, name, start, length, brightness }] }]`; `start` is the controller's first LED in `leds` and in the LED stream, a strip's `start` counts within its controller; the event follows layout changes and state changes (ready, retrying, ...) |
| `performance-update` | one LED report (type 3) per controller: `id` = position, `param1` updates/s, `param2` written, `param3` submitted, `param4` dropped since the last report, plus `controller`, `controllerName`, `ledCount`, `state`, `latencyMs` |
| `leddevice identify` `blink` | `params: { blinkIndex, controller?, hold? }` or `{ stop: true, controller? }`: `controller` is an id (or a position); without it `blinkIndex` counts over all LEDs (old clients, one controller: unchanged). `hold` keeps the LED cycling red, green, blue until the next blink, a stop or 120 s (`BlinkPattern.HoldTimeout`). Replies `{ controller, blinkIndex, hold }` (a stop: `{ controller, stopped }`); an LED that doesn't exist, a controller that isn't running, or a held output (first run, HyperHDR running) is an error. See [docs/protocol.md](../protocol.md#finding-an-led-leddevice-identify-leddevicetype-blink) |
| `ledcolors` stream | `leds` covers every controller's LEDs; each message and the `ledstream-start` reply carry `controllers: [{ id, start, length }]`; each message carries `share`, the share of the full output the brightness left in the frame (`ColorProcessor.OutputShare`, 0..1), so the previews can keep a dim frame readable; it is the latest submitted frame's while smoothing still fades toward it (the UI eases it in), and with zones the larger of the screen's and the static colors' |
| `serverinfo.profile`, `serverinfo.dismissedProfile`, event `profile-update` | the active profile `{ id, name, manual }` or null; the profile dismissed while its programs run `{ id, name }` or null; the event carries `{ profile, dismissed }` |
| `profile` | `getstate` (`{ profile, dismissed }`), `select` `{ id: string \| null }` (null: choose by the running programs; saved as `presets.selected`), `dismiss` (turns the active profile off, see [Profiles](#profiles-the-apps-presets)); both reply `{ profile, dismissed }`; `programs` (admins: the programs with a window, `{ programs: [{ exe, title }] }`, one per executable, sorted, from `IProgramList`: `EnumWindows` on Windows without tool, owned or cloaked windows, `Process.MainWindowTitle` elsewhere). See [docs/protocol.md](../protocol.md#profiles-profile) |

## UI

- **Layout page** (`ui/`): saves layouts with any number of controllers (`saveLayout` sends `layout`, plus the compiled
  `leds` for backends that don't compile layouts). It has two modes: Place (where strips run, their outputs and LED
  counts, matching with the lights, the test pattern; guided steps while no layout is saved) and Customize (what each
  part shows, per-strip brightness, turned-off LEDs, depth and softness, profiles). Matching and Find an LED (an expert
  tool, LEDs numbered from 1) light one LED with `leddevice identify` `blink` on that controller.
- **Devices page**: one card per controller ("device" in the UI) with its outputs (LEDs numbered from 1), an on/off
  switch, its state and its connection settings (the driver's schema form plus `device`'s shared settings, collapsed,
  saved with the layout).
- **Add device** takes WLED (outputs read from the device) or any other device type, with Search my network for the
  address, and then points to the Layout page. Lamp devices (Home Assistant, Zigbee2MQTT, LIFX, WiZ, Yeelight) get one
  LED per lamp in their list: the UI keeps the output's length equal to it instead of asking. The `file` driver is
  offered only with expert tools on.
- **Home** lists the LED updates and delay per controller when there are several.

## Measurements

Release build, synthetic source at 60 fps, smoothing off, file controllers writing to the null device, process CPU over
30-60 s, 24 logical cores, other load on the machine:

- the 168-LED reference layout on one controller: 0.8-1.1 % of one core;
- the same plus two test strips (30 and 20 LEDs) on two more controllers: 0.9-1.9 %; noise dominates the difference.

`PipelineProfile` (an explicit test, in process): 0.5 % (1 controller) vs 0.5-1.3 % (3 controllers); every controller
written at 60.0 (50.0 with smoothing) frames/s.

A live run with three file controllers (91 + 77 LEDs on A, 30 on B, 20 on C) wrote exactly 168 / 30 / 20 LEDs per line,
each line equal to its own slice of the live stream's frames and never to another position, all three at 60.06
updates/s, latency 0.03 ms.
