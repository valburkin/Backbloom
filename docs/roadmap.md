# Roadmap

Where Backbloom is going and what is open in today's app. Contributions toward any of it are welcome: open an issue to
discuss before starting on a larger item.

## Contents

- [Current state](#current-state)
- [Immersion](#immersion)
- [Today's app: validate in real use](#todays-app-validate-in-real-use)
- [Watch](#watch)
- [Layout, roles and profiles](#layout-roles-and-profiles)
- [Product and UX](#product-and-ux)
- [Platform and distribution](#platform-and-distribution)
- [Engineering](#engineering)

## Current state

The .NET backend and React UI cover HyperHDR's feature set as far as Backbloom keeps it. The app has been tested on a
real setup (an HDR monitor and an ESP32 WLED controller with a 91-LED monitor strip and a 77-LED ceiling strip), in
games and in music mode. What was merged since the last release is listed under Unreleased in the
[changelog](../CHANGELOG.md); the backend's current limitations are in [Known gaps](backend/known-gaps.md).

Backbloom is growing from ambient lighting into game immersion: lights, sound, haptics and wind driven by what happens
in a game, switched by presets (a game's preset turns on while it runs). [immersion.md](immersion.md) describes the
direction and the decisions; this page lists the steps, then what is open in today's app, roughly in priority order.

**In progress: guided hardware setup** ([guided-setup.md](guided-setup.md)): plan the strips (cuts, corners, power),
put a new WLED board on Wi-Fi over USB and set it up from Backbloom with guided wiring checks, and add room lights
(Hue, Nanoleaf, LIFX, WiZ, Yeelight, Govee over LAN, Home Assistant) from one search.

## Immersion

1. **LED strips** (done): screen and music mirrored on strips placed in the room, with profiles per program.
2. **Room and desk, and game events**:
   - The output model: outputs say what they take (per-LED colors, a few colors per lamp, zones or keys) and at what
     rate; room lights and peripherals placed in the room layout.
   - Presets and Looks in one app (built): a preset is a layout profile with an optional Look and optional programs
     that turn it on while they run; the capture rate rises to 60 fps while the Look's transitions are fast.
   - Room lights: Hue, Nanoleaf, LIFX, WiZ and Yeelight as room lights (the drivers exist), Govee (new, over LAN, or
     Bluetooth for older models). Slow mood by default (the music's beat with music, events or dimmed with a game
     preset), mirroring as an option. Govee first, as the most common screen-sync kit; test whether its LAN API can
     stream per-segment colors fast enough.
   - PC peripherals: OpenRGB, Razer Chroma and Corsair iCUE (check SDK terms).
   - Game events: generic picture and sound cues first, then official telemetry (Valve's Game State Integration, sim
     data outputs), then per-game profiles worked out offline, then small runtime models on the NPU or CPU, all within
     a GPU budget.
3. **Sound and haptics**: the audio setup per game (output device, spatial format, EQ preset, app volumes); a bass
   shaker signal built from the game's sound and events; later seat pads and vests.
4. **Wind**: fan controllers (DIY Arduino/ESP32 kits, ideally the protocols they already use; USB wind units), speed
   from telemetry, picture motion and event gusts.
5. **Head tracking**: webcam head tracking as a TrackIR/FreeTrack device for games that support it; head-tracked sound
   later.

Watched, not planned: scent devices (integrate one if it gets an API); to research: 3D figures in the room through
mixed-reality glasses.

## Today's app: validate in real use

- **HDR**: test Auto tone mapping with Windows HDR on (games, video) and confirm the LUT output looks right; consider a
  highlight roll-off instead of clipping at SDR white.

## Watch

- **WLED name lookups after a display change**: every reconnect once failed with "No such host is known
  (wled-a1b2c3.local:80)" for minutes, while a browser and a restarted Backbloom resolved the name at once. This is
  worked around (see [Host names](backend/led-drivers.md#host-names)); the cause in Windows' resolver isn't known. If
  the log shows "the system couldn't resolve the name ..., mDNS found ..." it happened again and the workaround caught
  it.

## Layout, roles and profiles

Built: strips are drawn in an unfolded room (walls, ceiling, floor; desk, monitor or TV drawn for
orientation), their bends split them into parts, and each part repeats a stretch of the screen's outline (the one it
faces, or one dragged into place), the whole picture, an effect or music, a color, nothing, or is a gap without LEDs.
Profiles give strips other roles while a program runs. The Layout page has two modes: **Place** (where strips really
are: what the screen is on, shapes or drawing, LED counts, matching with the lights, the test pattern) and
**Customize** (what each part shows, brightness, turned-off LEDs, profiles; Live colors shows the LEDs as they light
the room, from the saved layout). A part follows the screen (Screen edge or Screen average), plays an effect or a color
(a pick on Effects writes one into every part of the setup in use), or is off. Customize saves every change at once;
Place keeps its Save button. Without a saved layout, or after an import that only had LED positions, Place starts as
four guided steps (the first-run layout wizard in light form, no dialog).

Ideas and loose ends, roughly in priority order:

- **Smoother zone effects while the screen is captured**: today an effect on a part steps only when a screen frame
  arrives (20 fps by default); it keeps its speed but looks choppier than alone. Step zones on their own clock.
- **Smoothing per zone**: zones use the smoothing of whatever the main lights show; music on the ceiling over a
  smoothed screen gets the screen's smoothing.
- **More per profile**: brightness per strip, smoothing and intensity, the screen source on or off (e.g. a movie
  profile), hotkeys to switch profiles, a profile for when the PC is idle.
- **Running-dot calibration**: one LED runs along the strip and the user clicks at each bend, next to the
  step-by-step matching.
- **Guided steps that check themselves**: Place's first-run steps advance with Next; they could notice what's done
  (a strip moved, every strip matched, the test pattern seen) and offer to save at the end.
- **Position-aware effects**: effects that use the LEDs' places in the room (a wave across the room, rain from the
  ceiling) instead of the picture's edge; with room lights and peripherals placed in the room (immersion step 2) they
  take part too.
- **Areas in the editor**: the layout supports a part showing one area of the picture (`area` sections), but the
  editor offers only the whole picture; add a way to pick the area on the screen.
- **Room size** isn't editable since the room dialog was dropped (the default is 4 x 4 x 2.6 m); add dragging the
  walls if users need it.
- **Profiles and changed shapes**: when bends are added or removed in Everyday, a strip's sections in profiles are
  ignored (with a warning) instead of being split or joined along with the bends; Redraw could clear them.
- **Power limit** is applied to the screen and each zone separately, not across all LEDs.
- Effect previews and `serverinfo.leds` describe the saved layout, not the active profile.
- The program picker lists Store apps as `ApplicationFrameHost.exe`; resolve their real names.

## Product and UX

- **Lights off when the monitor is off** (opt-in): Windows doesn't notice a monitor switched off with its own button,
  so capture goes on with the last picture (observed: 30 fps, 0 new images/s for hours) and the strips keep its
  colors. Switch them off after the picture hasn't changed for a few minutes (a setting, off by default: a still
  screen while reading looks the same), or ask the monitor over DDC/CI whether it's on.
- **Show the capture crop on the screen**: while the crop settings are edited, draw the captured area on the monitor
  itself (a click-through, topmost overlay outlining it and dimming what's cut off), so the user sees where the capture
  goes instead of guessing from pixel numbers.
- **Monitor choice that survives a restart with several monitors**: the setting stores HyperHDR's GDI-name id, which
  Windows renumbers after a driver update; save the monitor's device path when it is picked (the fallback covers one
  monitor, or the running app). The Monitor list offers only Auto and the captured monitor (`systemGrabbers.modes`):
  list every connected one, and say when a fallback captures another than the setting names.
- **Phone remote**: Remote access (Settings) already opens the full UI on a phone through its QR code, but the UI
  is designed for the PC and looks poor on a phone. Add a phone-first remote: the everyday controls only (on/off,
  what's shown, brightness, the Look, presets), large touch targets, installable to the home screen (web app
  manifest; needs [https](#platform-and-distribution)) and staying signed in. Setup pages (Layout, Devices) stay on
  the PC. Later: a native app or widgets/quick tiles, if the web remote proves popular.
- Hotkeys (also for turning presets on and off).
- Phone-camera auto-mapping of LED positions.
- Tray menu dark theme.
- Static color and effect quick actions on Home; effect arguments (`effect.args`).
- Imported extra HyperHDR instances: restore their LED positions as additional controllers.

## Platform and distribution

- **Code signing** (removes the SmartScreen "unknown publisher" warning): get a certificate, fill in
  `packaging/build.ps1 -SignTemplate`.
- **https for remote access**: Backbloom serves http only, so the password and session cross the home network
  unencrypted. A self-signed certificate isn't the answer (phones show a full-page warning, and an installable phone
  remote needs a certificate the phone trusts), so the plan is a trusted one per install: a name under a domain
  Backbloom owns that points at the PC's local address, with a Let's Encrypt certificate (DNS challenge). It needs that
  domain and a small DNS service, so it comes with the phone remote. To weigh first: every such name is listed in the
  public certificate transparency logs (names must not identify the user), and some home routers refuse DNS answers
  that point to a local address (rebinding protection), so a fallback is needed. 0.2.0 showed https port and
  certificate fields that did nothing; they were removed.
- Run as a Windows service (`--service`) for headless setups.
- macOS and Linux: capture (ScreenCaptureKit / PipeWire), audio, tray and window behind the existing interfaces; the
  upstream HyperHDR C++ grabbers are reference implementations: they are in this repo's first commit (`118190e`, e.g.
  `git show 118190e:sources/grabber/osx/macOS/macOsGrabber.mm`) and in https://github.com/awawa-dev/HyperHDR.

## Engineering

- Hue API v2 channel identify; serial USB ids on Linux/macOS; Yeelight music server listener only when used.
- Screen hidden behind a higher-priority source isn't watched for capture loss.
- Default audio device changes are polled every 2 s (no `IMMNotificationClient`).
