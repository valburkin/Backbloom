# Known gaps

Limitations of the current backend, by area. Planned work is in the [roadmap](../roadmap.md).

## Effects and music

- An active screen hidden behind a higher source (the startup effect, an effect from the UI) isn't watched, so if
  capture stops meanwhile it goes inactive only 800 ms after it is visible again (HyperHDR kept capturing).
- Music mode follows a new default output within 2 s by polling (no `IMMNotificationClient`).
- The WASAPI capture runs on Windows only (macOS/Linux get no sound, so music effects stay dark).
- Effect arguments (`effect.args`) aren't supported.

## Drivers

- RGBW dithering uses the device's `refreshTime` rather than the smoothing clock.
- The Hue API v2 channel identify (flashing a channel of a running stream) isn't ported.
- Serial USB ids are read from the Windows device path (Linux/macOS list the ports without them).

## Screen capture

- Rotated monitors are captured in scanout orientation (HyperHDR's grabber mis-scaled them).
- The "please wait" image while the default LUT is built is not shown (frames pass without the LUT for ~0.3 s).
- `serverinfo.systemGrabbers.modes` lists only the captured monitor (Engine/Server should list `Monitors`).
- The duplication stays open while `SYSTEMGRABBER` is off.
- HDR content has no highlight roll-off (clipped at SDR white, as in HyperHDR).
- The desktop tests skip when the desktop delivers no frames (monitor off).

## Zones and profiles

- Zones are smoothed with the visible source's smoothing configuration (a music zone over a smoothed screen is smoothed
  too).
- While the screen shows, zone effects step with the captured frames (`systemGrabber.fps`, catching up so their speed
  is right).
- A music zone keeps the sound capture open while the output is held.
- The power limit applies to each zone's colors separately.
- Effect previews (`effects previews`) show effects on the layout as saved, not in zones or profiles.

## Multiple controllers

- The UI reads WLED outputs itself (browser fetch); other drivers get their LED count typed in (no driver reports it
  yet).
- Strip brightness scales the processed (gamma-encoded) values, not linear light.
- Old clients that save `leds` remove a one-controller layout (by design) and can't change a multi-controller one.
- The imported HyperHDR instances' own LED layouts aren't converted (they had their own `leds`).
- MQTT/Home Assistant brightness still applies to all controllers.
- The `instance` command still reports one instance (controllers aren't instances).

## Desktop shell

- Only Windows has a tray, window and power events; macOS/Linux run headless.
- The tray menu uses the light system menu style (no dark mode).
- With `--bind` to a single network address, the window reaches the server through that address, so it counts as
  another device and signs in.
- A `--service` process in a console isn't told about a logoff (only console close and shutdown are handled), and
  `--service` can't run as a Windows service (session 0) yet.
- Brightness from Home Assistant scales its static color only; the screen keeps the configured brightness.
