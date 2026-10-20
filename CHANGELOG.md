# Changelog

What changed in each Backbloom release, written for the people who use it. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

Every change merged into `main` adds its entry under **Unreleased** (in the same commit), in one of the groups
**Added**, **Changed**, **Fixed**, **Removed**. A release turns Unreleased into its version section; that section is the
release note. See [docs/development.md](docs/development.md#branches-and-releases).

## Unreleased

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
