# Guided hardware setup (backend)

The backend side of the [guided hardware setup](../guided-setup.md), which also documents the protocol. It is the
`hardware` command (`Backbloom.Server/Rpc/Commands/HardwareCommand.cs`, admins only) over the parts below.

## Contents

- [Parts](#parts)
- [Safety rules](#safety-rules)
- [Tests](#tests)
- [To verify on real hardware](#to-verify-on-real-hardware)

## Parts

| Part | Where | Does |
|---|---|---|
| Monitor sizes | `IDisplayList` (Engine/Platform), `WindowsDisplayList` (Platform.Windows), `Edid` | the active monitors from `QueryDisplayConfig` and their size from the EDID Windows keeps under `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY` (opened read-only): the first detailed timing descriptor's millimeters, else the basic size in centimeters, swapped for a monitor turned to portrait; null when unknown. Elsewhere `NoDisplayList` |
| Improv Wi-Fi | `ImprovProtocol`, `ImprovClient`, `UsbBoards` (Devices/Setup) | the Improv serial protocol v1 (packets `IMPROV`, version, type, length, data, checksum = low byte of the sum; state, error, RPC command and result packets; current state, device info, scanned networks, Wi-Fi settings) over `ISerialPort`. `UsbBoards` opens only likely ESP ports no configured device uses, one at a time, at 115200 baud with DTR and RTS off, and closes them again |
| WLED setup | `WledSetup`, `WledStrips` (Devices/Setup) | reads and writes a WLED board; see [WLED setup in detail](#wled-setup-in-detail) |
| Pairing | `NanoleafDriver.PairAsync`, `PhilipsHueDriver.CreateAreaAsync`, `HomeAssistantDriver.GetPropertiesAsync` / `IdentifyAsync` | Nanoleaf tokens (`POST /api/v1/new`), Hue entertainment areas (API v2 `entertainment_configuration`), Home Assistant's lights and a flash, through `leddevice getProperties` / `identify` |
| Govee | `GoveeDriver`, `MulticastProbe` | see the [driver table and Discovery](led-drivers.md#discovery) |
| Board search | `BoardFinder` (Devices/Setup) | see [Board search](#board-search) |
| This PC's Wi-Fi | `IWifi`, `NoWifi` (Devices/Setup), `WindowsWifi` (Platform.Windows) | wlanapi.dll (`LibraryImport`; TerraFX has no WLAN API): the current connection, a scan, a temporary per-user profile ("Backbloom setup …", manual, never overwriting) to join a hotspot, back to the previous profile, and deletion of the temporary profiles only. Windows 11 24H2+ refuses the scan and the current connection without location access (`ERROR_ACCESS_DENIED`, reported as `blocked: "location"`) and shows a one-time location prompt per app the first time; joining and leaving aren't affected. The test host registers `NoWifi`; tests use `FakeWifi` |

### WLED setup in detail

`WledSetup` and `WledStrips`:

- **Reading.** `/json/info` and `/json/cfg` are read and mapped to the protocol's shape.
- **Writing.** `POST /json/cfg` writes the outputs, the name, the Wi-Fi in `nw.ins`, and the limit both as
  `hw.led.maxpwr` and split over the outputs' `maxpwr` by LED count (each at least its share of the 100 mA WLED 0.15
  keeps for the ESP), so WLED 0.14, 0.15 and 0.16 each enforce it. A restart follows when WLED needs one.
  - A write the board applies without a restart returns once `/json/info` reports the new LED count, plus 500 ms
    (WLED rebuilds its outputs black a moment later, wiping a check lit before that).
  - Written outputs turn WLED's "turn on at boot" off (`def.on`).
  - A write that makes an output shorter turns every LED black first (the LEDs past its new end would keep their
    colors); the state is saved for `off` like a check's.
- **Checks.** The checks use `seg.i` on `/json/state` (other segments off). The state is saved on the first check
  without the segments' bounds, restored on `off`, and forgotten 30 min after the last check. White at full brightness
  is used only when the tested output itself is limited.
- **Identify.** `IdentifyAsync` (`wled-identify`): three dim white flashes of the whole board, then its state back.
- **Finding the strips** (`ProbeStartAsync` / `ProbeEndAsync`, `WledPins`):
  - the probe saves the board's state for `off` before it starts, dark (the found strips start dark), and its LED and
    boot settings (`def`) for `keep: false`;
  - the LED settings are saved in memory, then one WS281x output is set per candidate pin of the chip's round (2000
    LEDs over the candidates, 60 to 300 each), without the pins used by buttons, IR, relay, I2C or SPI, the limit kept;
  - the board restarts, and once it is back the first 20 LEDs of every candidate are lit dim white, the rest black;
  - before 0.15 an ESP32 gets at most 8 test outputs;
  - at the end the settings are restored or forgotten; they are kept 30 min after the last step. `keep: true` only
    forgets them (the found strips are written first, and a write with fewer or shorter outputs turns every LED black
    first).
- **Hosts.** `WledHosts` accepts any host name, but only addresses of private, link-local and unique-local networks
  (4.3.2.1 while this computer is on a WLED access point).
- **Presets.** The strip presets and WLED's type and color order numbers (API values from WLED's `const.h`; no WLED
  code).

### Board search

`BoardFinder`:

- **`hardware find`** searches USB (Improv), the network (mDNS `_wled._tcp` + `/json/info`) and the boards' hotspots (a
  Wi-Fi scan for "WLED…" networks) in parallel, merged into one entry per board. Each board carries what tells it apart:
  the port and its Windows name, Improv's firmware, version, chip and device name; the mDNS host name, MAC and LED
  count; a hotspot's signal.
- **`connect-wifi`** uses Improv over USB, or a short visit to the board's hotspot (temporary profile, "wled1234" then
  open, `nw.ins` to 4.3.2.1, always back to the PC's network in `finally`), then finds the board at home by its MAC.
  Progress is reported with `hardware-progress-update` events.
- **Saved passwords.** `wifi.savedPassword` and `useSavedPassword` use the key Windows keeps for the current network
  (`WlanGetProfile` plaintext key) for that call only; it is never returned, logged or stored.

## Safety rules

- **Wi-Fi passwords** go only to the board: they aren't logged (the log names the network), stored or echoed.
- **Concurrency.** The steps that talk to a board run on their own over a WebSocket (the reply comes when they're done,
  other requests of the connection go on) with their own time limits. A connection's `wled-*` steps run one after the
  other in the order they came (an `off` right after a check never overtakes it). The USB steps share one lock, so two
  requests never open a port at once, and so do the WLED writes and checks.
- **Hotspots.** One hotspot visit at a time (a second is refused), only to a network whose name starts with "WLED",
  with WLED's password when the scan saw one and open when it saw none.
- **No redirects.** The setup's HTTP client never follows redirects (every address it contacts passes `WledHosts`).
- **Boards in use.** `wled-write`, `wled-test` (except `off`), `wled-identify`, `wled-probe-start` and `wled-probe-end`
  with `keep: false` refuse a board that a connected (or connecting) WLED, DDP, E1.31 or Art-Net device of the layout
  sends to (by its `host`, names resolved, falling back to their last address; also when a connected device's host has
  no known address), and a board in realtime mode.
- **Errors** name the host only ("Couldn't reach &lt;host&gt;"), never socket details.

## Tests

Tests run against fakes only (`tests/Shared`):

- `FakeImprovBoard` on a fake serial port;
- `FakeWledBoard` in process or over HTTP on 127.0.0.1 (the setup's address policy gets a test-only allowance for
  loopback);
- a fake Govee light on loopback;
- fake discovery and monitor lists; `Ctx.Discovery` in the device tests always has a fake multicast probe.

## To verify on real hardware

Before release:

- that opening the port with DTR and RTS off doesn't reset common boards (CP210x, CH340 and the ESP32-S2/S3/C3's own
  USB);
- WLED's Improv answers and Wi-Fi join over USB;
- the write and restart policy on WLED 0.14 and 0.15 (per-output and global limiter fields);
- the checks' `seg.i` and the state restore;
- the hotspot visit (joining WLED-AP with a temporary profile, the way back with and without location access, the
  board found at home by its MAC);
- the location prompt on Windows 11 24H2;
- Nanoleaf pairing, Hue area creation, and Govee's rate limit and discovery through Windows Firewall.
