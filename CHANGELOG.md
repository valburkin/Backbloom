# Changelog

What changed in each Backbloom release, written for the people who use it. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

Every change merged into `main` adds its entry under **Unreleased** (in the same commit), in one of the groups
**Added**, **Changed**, **Fixed**, **Removed**. A release turns Unreleased into its version section; that section is the
release note. See [docs/development.md](docs/development.md#branches-and-releases).

## Unreleased

### Added

- **Room layout**: place your LED strips where they really are, on a drawing of your room unfolded around the screen
  (walls, ceiling and floor, with your desk and monitor or TV). Pick a ready-made shape or draw a strip yourself, drag
  its bends, and mark gaps where a strip is cut or runs around something. Bends snap to the screen and the furniture.
- **See and change what each LED repeats**: the selected strip is colored like a rainbow, and so is the part of the
  screen each LED shows; drag the dots on the screen's edge to change it.
- **Roles for strips and parts of them**: besides the screen next to it, a strip or any part of it can show the whole
  screen, play an effect or react to music on its own, glow in one color, or stay off.
- **Profiles**: other roles for your strips while a game or app runs, switched on by itself when the program starts;
  pick the program from the ones that are open and try a profile without starting it.
- **Match with your lights**: light one LED at a time and show where each corner is, so the LEDs land where they really
  are even if the drawing isn't to scale.
- **Performance recording**: Backbloom notes its CPU and memory use, the frame rate and how each controller keeps up
  every 10 seconds, in the `logs` folder next to its log, so a stutter or slowdown during a game can be looked into
  afterwards. If Backbloom crashes, the last 30 minutes are saved with the crash. The log also says when a controller
  can't keep up.
- **LED power and controller health**: the recording also shows how bright each strip runs and the current that
  means, and for WLED controllers what they report themselves: their power use and limit, their own brightness,
  Wi-Fi, temperature (with a temperature sensor) and restarts. The log warns when a controller restarts, runs hot or
  is held at its power limit, and says when its brightness was changed in the WLED app. After a restart, Backbloom
  sets the controller up for streaming again.
- **GPU use**: the recording shows how much of the graphics card Backbloom and the whole computer use, and the video
  memory.

### Changed

- **Find an LED** keeps the LED lit (cycling red, green and blue) until you pick another one or turn it off, and works
  while the lights are off.
- Effect previews and the live preview show your LEDs where they are in the room.
- Layouts saved by earlier test builds are rebuilt around the screen from your LED positions the first time you open
  the Layout page; drag the strips to where they are and save.
- The log no longer calls WLED's power limiter an error: it protects the LEDs and the power supply from drawing too
  much.

### Fixed

- Finding an LED did nothing while the lights were off or nothing was shown.

## 0.1.0 – 2026-10-20

First release.

### Added

- **Screen sync**: the picture on your monitor or TV, including HDR (automatic tone mapping), is mirrored on the LEDs
  around it in real time, with black border detection and multi-monitor capture.
- **Color**: calibration per channel, gamma, color temperature, brightness and saturation, a minimal backlight and a
  power limit.
- **Smoothing**: six ways to soften color changes, with an anti-flicker filter.
- **Effects and music mode**: rainbow swirls, mood blobs, breath, candle and nine music effects (spectrum, VU meter,
  beat ripple, waterfall and bass glow are new), with previews on your own LED layout. Music effects listen to what the
  PC plays without any setup.
- **Layouts with several controllers**: strips on different LED controllers (for example behind the monitor and on the
  ceiling) in one layout, edited in the UI with undo and redo.
- **Devices**: WLED, DDP, E1.31, Art-Net, Philips Hue, Nanoleaf, LIFX, WiZ, Yeelight, Home Assistant, Zigbee2MQTT,
  Adalight-compatible USB controllers, Skydimo, SP616E and Robobloq SyncLight; Find discovers most of them.
- **Desktop app**: tray menu, a window with the settings, optional start with Windows, sleep and lock handling, and
  automatic updates.
- **First run**: HyperHDR settings are imported and a welcome window shows what was found; the LEDs stay off until you
  press Start lighting, and while HyperHDR runs Backbloom waits for it to quit.
- **Control from other devices** (off by default): phones and other computers sign in with a password you choose;
  JSON-RPC and MQTT with Home Assistant discovery.
- Settings save automatically; a failed start or a crash is reported in a message box and the log.
