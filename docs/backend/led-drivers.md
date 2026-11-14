# LED drivers and discovery

The LED drivers, the contract they implement, how they resolve host names and retry, the libraries they use, and
device discovery on the network, USB serial and HID.

Every driver of HyperHDR's C++ code is ported (`sources/led-drivers`). The settings keys and schemas are HyperHDR's
(`Backbloom.Devices/Schemas`, reformatted), so imported configurations keep working. A driver only speaks its protocol
(`ILedDriver`: open, write a frame, close); `LedDeviceRunner` handles the lifecycle once for all of them.

## Contents

- [Drivers](#drivers)
- [Driver contract](#driver-contract)
- [Setting tiers](#setting-tiers)
- [Host names](#host-names)
- [RGBW](#rgbw)
- [Libraries](#libraries)
- [Discovery](#discovery)
- [Driver tools: `leddevice`](#driver-tools-leddevice)
- [Deliberate differences from HyperHDR](#deliberate-differences-from-hyperhdr)

## Drivers

| Type | Group | Transport | Notes |
|---|---|---|---|
| `wled` | network | REST + UDP | DRGB/DNRGB; discovery: mDNS `_wled._tcp`; reads WLED's power settings on connect and its status every 10 s while connected ([telemetry](performance-recording.md#controller-telemetry-wled)) |
| `ddp` | network | UDP 4048 | RGB, classic RGBW (`rgbw` + `white_algorithm`), RGBW by the Infinite Color Engine |
| `udpe131` | network | UDP 5568 | E1.31/sACN, 512 or 510 channels per universe, CID from the settings or random |
| `udpartnet` | network | UDP 6454 | ArtDmx, channels per fixture, splitting, RGBW (ICE) |
| `philipshue` | network | REST (v1 http, v2 https) + DTLS 1.2 PSK UDP 2100 | REST lights, Entertainment v1 groups and v2 configurations; discovery: mDNS `_hue._tcp`, else SSDP "IpBridge"; `getProperties` reads the bridge (API v2 for a `/clip/v2` filter), with `pair: true` it creates a username + client key, with `createArea` an entertainment area (see [guided-setup.md](../guided-setup.md#protocol-hardware)) |
| `nanoleaf` | network | REST 16021 + UDP | panel order (top/bottom, left/right, start panel), External Control v2; discovery: SSDP; `getProperties` with `pair: true` creates a token (`POST /api/v1/new`, the controller in pairing mode) |
| `lifx` | network | UDP 56700 | SetColor (HSBK, unrounded colors) per lamp, SetPower; discovery: GetService broadcast |
| `wiz` | network | UDP 38899 | JSON setPilot per lamp; discovery: getPilot broadcast |
| `govee` | network | UDP 4003 (LAN API) | Backbloom's own: `colorwc` per light, at most every `updateInterval` (100 ms) and only when the color changed (all every 2 s), `turn` + `brightness` on open, off on close; discovery: `scan` to 239.255.255.250:4001 from port 4002 (see [Discovery](#discovery)); identify: a short blink, then the light as it was (`devStatus`); on/brightness commands 200 ms apart. The lights need LAN Control turned on in the Govee app |
| `yeelight` | network | TCP 55443 + music mode | the lamp connects back to Backbloom's music mode server; command quota wait; discovery: SSDP port 1982 |
| `home_assistant` | network | REST | `light.turn_on` per entity (RGB or hue/saturation), constant brightness, state restore; discovery: mDNS `_home-assistant._tcp`; `getProperties { host, token }` lists the `light.*` entities (`GET /api/states`), `identify { host, token, entityId }` flashes one (`flash: "short"`) |
| `zigbee2mqtt` | network | MQTT | `zigbee2mqtt/<lamp>/set`; lamps switched off on shutdown; discovery: the bridge's retained device list |
| `adalight` | serial | System.IO.Ports | Ada, AWA (Fletcher checksum, white calibration, RGBW by ICE), LightBerry APA102; "auto" port, ESP/Pico handshake and goodbye |
| `skydimo`, `sp616e` | serial | System.IO.Ports | |
| `synclight` | HID | HidSharp | Robobloq SyncLight: RB/SC frames in 65-byte reports, per-segment merging, LED count read from the controller |
| `file` | debug | file | bytes, deep colors (`infiniteColorEngine`) or ICE RGBW lines |

## Driver contract

- **Float colors.** `IFloatColorDriver` takes the processed colors before 8-bit rounding (LIFX, Hue Entertainment, and
  DDP/Art-Net/Adalight/file with RGBW by the Infinite Color Engine; port of `writeInfiniteColors`). The runner passes
  them in RGB order with the refresh interval.
- **Resend rate.** `ILedDriver.EffectiveRefreshInterval` lets a protocol set the resend rate (Hue Entertainment 20 ms,
  Hue REST 100 ms per light, none for Yeelight / Nanoleaf).
- **Lost controllers.** A driver that loses its controller while streaming (serial port unplugged, too many write
  timeouts, all Yeelights in error, HTTP errors) throws `DeviceLostException`. The runner goes to `WaitingToRetry` and
  reopens it with the driver's retry policy (HyperHDR's `setInError` + `setupRetry`).
- **Retries.** Network drivers (WLED, Hue, Home Assistant, Yeelight, and those with `RetryPolicy.Default`, which also
  covers the SyncLight HID strip) don't give up after their `maxRetry` quick retries as HyperHDR did: they go on every
  30 s (`RetryPolicy.ThenEvery`; logged once, then at debug level), so a controller that was away for a while (or a USB
  strip plugged in again) is used again by itself. Serial and file devices still give up, so the UI describes their
  `maxRetry` differently (`edt_dev_max_retry_serial`).
- **JSON.** JSON/REST drivers write their JSON with sorted keys, as `QJsonDocument` did.

## Setting tiers

Every property of a driver schema (and of `device`, `mqtt`) has a tier in `access` (see
[docs/ui-guidelines.md](../ui-guidelines.md#setting-tiers)):

| Tier | Used for |
|---|---|
| none (essential) | what is needed to connect: the address or port, the lamps or lights, the universe of Art-Net/E1.31 |
| `"advanced"` | fine-tuning the UI puts under More options: WLED brightness, RGBW, network ports, Hue streaming and black handling, transitions |
| `"expert"` | what only expert tools show: `maxRetry`, ICE parameters and white calibration, `cid`, serial handshakes and delays, timeouts, `verbose`, `debugLevel`, `refreshTime` |

The UI makes color order expert for WLED and DDP, which order the colors themselves. Titles are keys of
`ui/src/schema/text.ts`; Backbloom's own keys (`edt_dev_spec_hueBridge_title`, `edt_dev_spec_lampColorModel_title`, …)
replace HyperHDR keys whose text didn't fit. Serial devices default to the `auto` port (sp616e and Skydimo had the Linux
`ttyACM0`).

## Host names

`HostResolver` (the default `DriverContext.ResolveHost`) tries, in order:

1. the system resolver (3 s at most);
2. for a `.local` name it can't resolve, an mDNS A query of its own (`MdnsService.ResolveAsync`, 1.5 s);
3. when both fail, the last address the name had.

This is not in HyperHDR: Windows' resolver once kept failing for a WLED controller's `.local` name in the running
process ("No such host is known"; HyperHDR showed the same) while a browser and a restarted process resolved it at once.
The WLED driver resolves its host once per connection and sends both its HTTP requests and its stream to that address,
so `HttpClient` never resolves the name itself (requests carry the address as their `Host`, which WLED ignores). The
last address is only a fallback: after a DHCP change it may belong to another device, and requests reach that one until
the name resolves again.

## RGBW

`RgbwConverter` (classic 8-bit algorithms, HyperSerial calibration tables) and `InfiniteRgbwEncoder` (stateful,
dithered) reproduce HyperHDR's float operations in the same order and match the exported fixtures exactly.

## Libraries

All permissively licensed, maintained, and pinned in `Directory.Packages.props`:

| Package | License | Used for | Why |
|---|---|---|---|
| BouncyCastle.Cryptography 2.7 | MIT | DTLS 1.2 PSK client (Hue Entertainment) | .NET has no DTLS; BouncyCastle is the standard managed implementation and needs no native OpenSSL |
| MQTTnet 5.2 (+ MQTTnet.Server in tests) | MIT | `Backbloom.Mqtt.MqttConnection` | the .NET Foundation MQTT client; tests run a real broker in process |
| HidSharp 2.6 | Apache-2.0 | HID enumeration and reports, serial port names/USB ids | managed, cross-platform (hid.dll, hidraw, IOKit), so no platform project is needed; TerraFX would cover Windows only and need a Linux/macOS port later |
| System.IO.Ports 10.0 | MIT | serial I/O | the platform's serial API |

## Discovery

`Backbloom.Discovery` is passive: it only sends queries (mDNS PTR, SSDP M-SEARCH, the LIFX/WiZ broadcast probes) and
lists what answers; nothing found is configured or contacted by the discovery itself.

It runs only when asked (`leddevice discover`: the UI's Search my network in Add device, or Search in a device's
connection settings, explained once before the first network search) and listens nowhere in between. Every query goes
out from `QuerySockets`: one UDP socket per local IPv4 address, bound to that address and an ephemeral port, closed
when the query ends. The answers come back to that port (Windows Firewall lets unicast answers to a multicast or
broadcast query in for a few seconds without an inbound rule), so nothing is bound to 0.0.0.0 or to a well-known port.
Measured on a development machine: at startup only `127.0.0.1:<port>` (TCP); during a search one ephemeral UDP port per
interface address; afterward loopback only again.

- **mDNS** is implemented here (about 900 lines: DNS message codec, cache with TTLs and goodbyes, responder) instead of
  using a library: the maintained .NET options are browse-only (Zeroconf) or unmaintained since 2019
  (Makaretu.Dns.Multicast), and the protocol subset needed is small and fully testable without sockets
  (`IMdnsTransport`). A browse opens its own `UdpMdnsQueryTransport`: queries from a port other than 5353 are RFC 6762
  legacy unicast queries and carry the QU bit, so responders answer the querier directly. The responder transport
  (`UdpMdnsTransport`: UDP 5353 shared with the OS responder, 224.0.0.251 joined per interface) exists only while the
  web UI is advertised. Own addresses are never reported.
- **Advertising.** The web UI is advertised as `<host>:<port>._backbloom-http._tcp.local` only while the server listens
  on the network (remote access on, or `--bind` with a network address); a goodbye is sent when it stops.
- **SSDP**: one M-SEARCH (HyperHDR's message, MX 1) per interface and every answer within 3 s, headers parsed like
  `SSDPDiscover`.
- **Govee** (`MulticastProbe`) is the one exception to ephemeral ports. Govee lights answer the LAN API's `scan` at port
  4002 of the querier's address, not at the query's port, so for the 2 s of a search one socket per multicast-capable
  interface address is bound to `<address>:4002` (exclusively; an address where another program holds 4002 is
  skipped), and the scan to 239.255.255.250:4001 goes out from that socket. The answers are then replies to Backbloom's
  own query from the light's address, which Windows Firewall lets in like the other searches' answers. Still to be
  confirmed on a real network before release: whether Windows treats an answer to the fixed port as such a reply (a
  firewall prompt or a missing answer would show it); if not, the search needs an inbound rule for UDP 4002 on private
  networks, set by the installer.
- **Serial ports**: HidSharp's serial device list (friendly name, USB ids from the Windows device path) plus
  `SerialPort.GetPortNames()`. **HID devices**: HidSharp with the top-level usage page/usage.

## Driver tools: `leddevice`

- `leddevice discover` replies `{ ledDeviceType, devices: [...] }` with HyperHDR's fields for each driver plus `value`
  (what to put into the address/port setting) and `name` for the UI.
- `getProperties` replies `{ properties }`.
- `identify` with `ledDeviceType: "blink"` blinks an LED (see [docs/protocol.md](../protocol.md#finding-an-led-leddevice-identify-leddevicetype-blink));
  otherwise it runs the driver's identify (Hue alert, WiZ pulse, LIFX blue, Yeelight color flow, Nanoleaf,
  Zigbee2MQTT).

## Deliberate differences from HyperHDR

1. Art-Net: the datagram carries the even data length the header announces (HyperHDR sent one byte less for odd
   lengths), and the RGB sequence never sends 0 (it wrapped to 0 once per 255 frames).
2. E1.31: the universe is 16-bit (HyperHDR stored it in a byte).
3. Nanoleaf: the stream port from the Light Panels' answer is used as is (HyperHDR truncated it to a byte).
4. Yeelight reads the lamps' state only with "restore original state" (HyperHDR read it on every switch-on, costing the
   command quota).
5. Serial drivers send three black frames on close (one by the runner, two by the driver), as HyperHDR did; HID
   SyncLight sends one.
6. The LAN HTTPS client accepts unvalidated certificates only for private, link-local and `.local` hosts (HyperHDR
   ignored TLS errors for every host).
7. Discovery results carry `value` / `name` for every driver (HyperHDR's shapes are kept alongside).
