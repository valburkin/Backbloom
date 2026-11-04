# Changelog

What changed in each Backbloom release, written for the people who use it. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

Every change to the app merged into `main` adds its entry under **Unreleased** (in the same commit), in one of the groups
**Added**, **Changed**, **Fixed**, **Removed**. A release turns Unreleased into its version section; that section is the
release note (the landing page in `site/` isn't part of a release and has no entries). See
[docs/development.md](docs/development.md#branches-and-releases).

## Unreleased

### Added

- **Set up new LED strips without the board's web page**: Add device → LED strips on a WLED board. One Search finds
  the board over USB, on its own Wi-Fi or on your network, and puts it on your Wi-Fi (with the password this PC knows,
  if you like). Then Backbloom finds which connection each strip is on by lighting them one at a time, asks the strip
  type and the power supply (and keeps the brightness within it), and walks you through the last LED, the colors and
  the power at the far end. A planner shows where to cut and join a strip around a monitor or TV.
- **Room lights in the same place**: Govee lights on your network, and Philips Hue, Nanoleaf and Home Assistant lights,
  found and added from Add device → Room lights.
- **A first-run setup that ends ready to use**: after your lights, say what each one does (every light follows the
  screen's average colors until you choose; Screen edge lets you draw the strip's route and checks it on the strip),
  pick the feel and add your games. New lights start at 20% brightness.
- **Live colors on the Layout page**: in Customize, show every LED in the color it lights your room right now,
  instead of the rainbow that marks what each LED follows.
- **`--capture-image <file>`** (for developers): a still picture as the frame source instead of the screen, so
  screenshots and demos show known colors.
- **Ambient and Game mode**: two apps in one, switched at the top of the sidebar. Ambient is for movies, music and
  everyday use, with screen sync, music, effects and colors. Game is made for playing: the lights follow the game on
  your screen, react faster with richer colors, dark scenes stay dark, and the screen is read more often. Its
  Intensity (Subtle, Vivid or Intense) and its brightness are its own (it starts at Ambient's brightness until you
  change it while playing), so your everyday settings stay as they are, and an effect, music or color you had on
  comes back when you switch to Ambient.
  Add your games on Game mode's Games page and Game mode turns on by itself while one runs, and off when it quits.
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

- **WLED power limits by version**: Backbloom reads a WLED board's current limit the way its WLED version enforces it
  (0.15 limits each output on its own), so the performance recording no longer reports a strip as limited when it isn't, or the
  other way round.
- **A fresh look**: the app now matches the Backbloom website, with new fonts, the pink-to-cyan Backbloom colors on
  the main buttons, switches and the brightness slider, sliders that fill up to their handle, and a soft glow at the
  top of every page. On Home the glow takes the colors your lights show right now.
- **Find an LED** keeps the LED lit (cycling red, green and blue) until you pick another one or turn it off, and works
  while the lights are off.
- Effect previews and the live preview show your LEDs where they are in the room.
- Layouts saved by earlier test builds are rebuilt around the screen from your LED positions the first time you open
  the Layout page; drag the strips to where they are and save.
- The log no longer calls WLED's power limiter an error: it protects the LEDs and the power supply from drawing too
  much.
- **Mood replaces Intensity in Ambient**: Relaxed, Natural and Rich (before: Subtle, Moderate, High, Intense) on Home
  set how the room feels, including the speed of the default smoothing style, as does Transitions on the Colors page
  (before, they only changed the colors). Natural is the default. Colors fine-tunes the same values and says which
  Mood they match. The other smoothing styles moved under More options.
- **A simpler app**: everyday settings are in view and the rest is tucked away.
  - Fewer sections, in two groups: what belongs to the mode (Home, Colors, and Effects & Music or Games) and, at the
    bottom of the sidebar, what both modes share: Setup (Layout, Devices, Screen and Calibration) and Settings. White
    balance, contrast, per-color calibration and the power limit moved to Setup → Calibration. Their names show in the sidebar at
    the default window size.
  - Fine-tuning sits under **More options** at the end of each section; it says how many values you changed and can
    put them back. Diagnostics and technical settings (performance numbers, logs, protocol details, Find an LED)
    appear only with **Settings → General → Show expert and diagnostic tools**.
  - Clearer words throughout: devices instead of controllers and drivers, Screen sync, transitions in seconds, LEDs
    numbered from 1 everywhere.
  - Easier to read: larger text, more contrast for hints and input borders, fewer nested boxes.
- **Setup when Backbloom starts for the first time**: three short steps (welcome, your lights, turn on) instead of a
  table of settings. Add your device right there, then Backbloom leads you to placing your strips.
- **Adding a device**: search your network for WLED from the Add device dialog instead of typing its address; devices
  you already added are marked. Afterwards Backbloom points you to placing your strips.
- **Layout has two modes**: Place (where your strips are, with four guided steps when there's no layout yet) and
  Customize (what each part shows, brightness, profiles). Trying a profile is now called Preview, and Home can stop it.
- **Home** shows what matters: on/off, what's shown, brightness and Mood (Intensity in Game mode), a warning when a device can't be
  reached, and **Lights look wrong?**, which names the usual causes and takes you to the right setting. Brightness is
  set only here now (above 100% is an expert setting on Colors).
- **Music effects turn on sound input by themselves.**
- **Settings** has four tabs: General (now with Start with Windows), Remote access (password and network access
  together), Integrations (MQTT) and About; the log is under Support with expert tools on.
- **HDR**: Backbloom reads Windows' SDR content brightness for each monitor itself (it used to ask for a number of
  nits), and follows it when you change it.
- Calmer defaults: the startup effect is a short fade, screen capture runs at 30 frames per second, and the log keeps
  only warnings and errors.

### Fixed

- **Screen average lights flickered between colors** (e.g. yellow and green) in some scenes, most of all in Game mode:
  their color now changes more gently, while the lights along the screen edges stay as quick as before.
- Finding an LED did nothing while the lights were off or nothing was shown.
- **WLED lights stayed off after a display change** (a game starting, a resolution or HDR change): Backbloom no longer switches the
  controllers off when the picture is gone for a moment (they go dark for up to 10 seconds first), finds a WLED
  controller by its `.local` name itself when Windows can't, falls back to its last address, and keeps trying to
  reconnect every 30 seconds instead of giving up after a few minutes.
- **The lights stayed dark after a graphics driver update**: Windows gives the monitor a new number then, and the
  screen capture kept looking for the old one. It now finds the same monitor again, or uses the only connected one.
- Smoothing could flicker with a low update frequency or a very stiff spring set under Advanced.
- Clicking into a slider's number box and leaving it without typing no longer saves the value again.
- The Screen capture page had a switch that did nothing and could say capture was off while it was running; it's gone
  (turn the screen on or off from Home).
- Music could look selected on Home while the lights still showed the screen.
- Hint text and input borders were too faint to read comfortably.
- The brightness slider did nothing in Color mode (and for color zones), and the power limit didn't apply to them.
- **The brightness slider changed little above the middle**: it now follows how bright the lights look, so the whole
  range makes a difference (the middle gives about a quarter of the full light). With a power supply smaller than full
  white needs, 100% is the brightest the supply allows, and bright scenes still dim all along the slider instead of
  staying at the supply's limit.

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
