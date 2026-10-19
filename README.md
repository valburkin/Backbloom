# Overglow

Overglow is an ambient lighting service for TVs and monitors. It captures the screen (or a sound source),
maps the picture to the LEDs around your display and drives them in real time, with HDR-aware color
processing, smoothing and per-device color calibration. It is configured from a web UI served by the
service itself.

## Platforms

- **Windows 10/11 x64** — the primary, tested platform (DirectX 11 screen capture, including HDR displays).
- Linux (x64/ARM, X11 and PipeWire capture) and macOS (system screen capture) build from the
  same sources, but are not currently tested or packaged.

USB capture cards (Media Foundation/V4L2/AVFoundation) are supported by the sources but are off in the
Windows product build (`-DENABLE_MF=ON` enables them).

## LED devices

- Network: WLED, DDP, Philips Hue, LIFX, Nanoleaf, WiZ, Yeelight, Home Assistant,
  Zigbee2MQTT, E1.31 (sACN), Art-Net
- USB serial: Adalight-compatible controllers, Skydimo, SP616E
- USB HID: SyncLight
- File output (for debugging)

MQTT control and mDNS discovery are built in.

## Building

- Windows: [BUILDING-WINDOWS.md](BUILDING-WINDOWS.md)
- Linux/macOS: [BUILDING.md](BUILDING.md)

The new web UI lives in [`ui/`](ui) (React, `npm install && npm run build`); the original web UI in
[`www/`](www) is still served until the new one reaches feature parity.

## Credits

Based on [HyperHDR](https://github.com/awawa-dev/HyperHDR) by awawa-dev (MIT License).
Third-party components and their licenses are listed in [3RD_PARTY_LICENSES](3RD_PARTY_LICENSES).

## License

MIT, see [LICENSE](LICENSE).
