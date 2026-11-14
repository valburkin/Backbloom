# Changelog

What changed in each Backbloom release, written for the people who use it. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

Every change to the app merged into `main` adds its entry under **Unreleased** (in the same commit), in one of the groups
**Added**, **Changed**, **Fixed**, **Removed**. A release turns Unreleased into its version section; that section is the
release note (the landing page in `site/` isn't part of a release and has no entries). See
[docs/development.md](docs/development.md#branches-and-releases).

## Unreleased

### Changed

- **Remote access says it isn't encrypted**: the dialog that turns it on now tells you that other devices connect
  without encryption, so turn it on only on a network you trust. https, with a certificate that phones trust without a
  warning, is planned together with a phone remote (see the
  [roadmap](https://github.com/valburkin/Backbloom/blob/main/docs/roadmap.md#platform-and-distribution)).

### Removed

- **Remote access settings that did nothing**: under More options, Settings → Remote access showed a port field and a
  "Secure port (https)" field, and with expert tools on, certificate and key fields. Backbloom never read any of them:
  it doesn't serve https, and its port is set when it starts (18290 unless started with `--port`). Changing them had no
  effect, so they are gone. Nothing changes for devices that already connect: same address, same port, `http://` as
  before. The old values are deleted from your settings, including a key password if you typed one in (backups made
  before still contain it).

### Fixed

- **Credits**: Settings → About now also names uqr, which draws the remote access QR code. About and the third-party
  notices now name Vite and Rolldown too, whose small runtime helpers are part of the interface.
- **Last lines of the log on a busy PC**: when the PC was fully loaded (a game, a build), the last lines Backbloom
  wrote before quitting could be missing from its log file. Its log writer no longer waits behind other programs.
- **Keys in logs and error messages**: when a Nanoleaf panel or a Philips Hue bridge didn't answer, the log line and
  the error shown in the app could carry the key Backbloom uses to control it. They no longer do. If you posted a log
  or a screenshot of such an error from 0.2.0, edit the key out (the long code after `/api/` in an address); it works
  only on your home network. Pairing the light again gives Backbloom a new key but doesn't cancel the old one.

## 0.2.0 – 2026-11-13

First public release.

### Added

- **Open source**: Backbloom is now open source under the MIT License, developed in the open on GitHub. Downloads
  and automatic updates come from its GitHub releases, each with checksums and a build provenance attestation.
- **Get help from the app**: Settings > About > Report a problem opens a bug report on GitHub with your system
  details filled in; Ask a question opens GitHub Discussions.
- **Set up new LED strips without the board's web page**: Add device → LED strips on a WLED board. One Search finds
  the board over USB, on its own Wi-Fi or on your network, and puts it on your Wi-Fi (with the password this PC knows,
  if you like). Then Backbloom finds which connection each strip is on by lighting them one at a time, asks the strip
  type and the power supply (and keeps the brightness within it), and walks you through the last LED, the colors and
  the power at the far end. A planner shows where to cut and join a strip around a monitor or TV.
- **Room lights in the same place**: Govee lights on your network, and Philips Hue, Nanoleaf and Home Assistant lights,
  found and added from Add device → Room lights.
- **A first-run setup that ends ready to use**: after your lights, say what each one does (every light follows the
  screen's average colors until you choose; Screen edge lets you draw the strip's route and checks it on the strip),
  pick the Look and add your game to the Gaming preset. New lights start at 60% on the brightness slider (about a
  fifth of their full light).
- **Live colors on the Layout page**: in Customize, show every LED in the color it lights your room right now,
  instead of the rainbow that marks what each LED follows.
- **`--capture-image <file>`** (for developers): a still picture as the frame source instead of the screen, so
  screenshots and demos show known colors.
- **Presets**: saved setups of what your lights play and how they look. Everyday is your usual one; Gaming, Movie,
  Music and Cozy come ready-made, and you can add your own. Switch between them on Home or the Presets page: one is
  always in use, and whatever you change saves to it (it's still in use after a restart). A preset can also be used by
  itself while a program runs, such as your game, and Everyday comes back when it quits. Each preset can have its
  own Look and its own setup per strip (Edit strips in Layout).
- **Room layout**: place your LED strips where they really are, on a drawing of your room unfolded around the screen
  (walls, ceiling and floor, with your desk and monitor or TV). Pick a ready-made shape or draw a strip yourself, drag
  its bends, and mark gaps where a strip is cut or runs around something. Bends snap to the screen and the furniture.
- **See and change what each LED repeats**: the selected strip is colored like a rainbow, and so is the part of the
  screen each LED shows; drag the dots on the screen's edge to change it.
- **Roles for strips and parts of them**: besides the screen next to it, a strip or any part of it can show the whole
  screen, play an effect or react to music on its own, glow in one color, or stay off.
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

- **A new look** that matches the Backbloom website: new fonts, the pink-to-cyan Backbloom colors on the main
  buttons, switches and the brightness slider, lighter cards without heavy outlines, glowing buttons, choices that
  slide into place, softer fields and lists, and a soft glow at the top of every page (on Home, in the colors your
  lights show right now). Live previews glow and blend like the real light: dimmer when you turn the brightness down,
  but still easy to see.
- **What you pick stays**: Screen edges, Screen average, an effect, music or a color picked on the new **Effects**
  page is saved to the preset in use and plays on every strip, also after a restart; there's no temporary effect to
  stop any more. Picking one replaces what you customized per strip in Layout (Off parts stay off; Undo puts it
  back), and changing a strip in Layout → Customize changes only that strip. The Effects page keeps a live preview
  in view while you scroll; startup and idle effects moved to Settings → General.
- **Customize applies at once**: changes there are saved and shown right away, without Save (Place keeps it).
- **Clearer choices for what a strip shows** (Layout → Customize): a strip either **follows the Show** (what your lights
  show now, named on its card) or has **its own effect**, **its own color**, or is **off**. Screen edge and Screen
  average are a setting of following the Show, used with Screen sync.
- **WLED power limits by version**: Backbloom reads a WLED board's current limit the way its WLED version enforces it
  (0.15 limits each output on its own), so the performance recording no longer reports a strip as limited when it isn't, or the
  other way round.
- **Find an LED** keeps the LED lit (cycling red, green and blue) until you pick another one or turn it off, and works
  while the lights are off.
- Effect previews and the live preview show your LEDs where they are in the room.
- The log no longer calls WLED's power limiter an error: it protects the LEDs and the power supply from drawing too
  much.
- **The Look replaces Intensity**: Relaxed, Natural, Rich, Vivid and Intense (before: Subtle, Moderate, High, Intense)
  on Home set how fast and how rich the lights are, including the speed of the default smoothing style, as does
  Transitions on the Colors page (before, they only changed the colors). Natural is the default; Vivid and Intense keep
  dark scenes dark, and while a Look is that quick the screen is read at least 60 times a second. Colors fine-tunes the
  Look of the preset in use and says which Look it matches. The other smoothing styles moved under More options.
- **A simpler app**: everyday settings are in view and the rest is tucked away.
  - Fewer sections: Home, Effects, Presets and Colors, and at the bottom of the sidebar Setup (Layout, Devices, Screen
    and Calibration) and Settings. White
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
  Customize (what each part shows, brightness, and the strips of each preset).
- **Home** shows what matters: on/off, brightness, what plays now above a live preview (it follows the brightness and
  stays readable when the lights are dim), the preset in use and its Look, a warning when a device can't be
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

- **Effects and music now play on every strip**: a strip set to Screen average showed one flat color during effects
  and music (the average of the whole effect). Its role is for the screen now; effects run along it like on the
  others, and the effect previews show that.
- **Strip parts set to Off stayed dimly lit**: they are fully off now, whatever the color settings' minimum brightness.
- **Effect previews load faster** with Screen average lights.
- **Screen average lights flickered between colors** (e.g. yellow and green) in some scenes:
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
