# Access, first run and network

Who may use the API, what holds the lights back on a first install or while HyperHDR runs, and how network access is
opened only when the user asks for it.

A first install must not take over the lights or the network by itself. Driving the LEDs at once after a HyperHDR
import would mean two senders (flickering) while HyperHDR still runs, and listening on every interface would trigger an
unexplained Windows Firewall prompt. The rules below prevent both.

## Contents

- [Access: this computer and other devices](#access-this-computer-and-other-devices)
- [Lighting gate](#lighting-gate)
- [First-run setup](#first-run-setup)
- [No lights yet](#no-lights-yet)
- [Network access](#network-access)
- [Protocol: `setup`](#protocol-setup)

## Access: this computer and other devices

`Backbloom.Server/Auth/LocalAccess` decides, per connection:

| Peer | Rights |
|---|---|
| loopback: 127.0.0.0/8, `::1`, IPv4-mapped `::ffff:127.x` | full access (API and configuration), no password |
| anything else (only reachable with remote access on, or `--bind`) | none until it signs in with the password, a user token or an API token; refused (403) while no password is set |
| MQTT `JsonAPI` | API rights, no configuration access (anyone who can publish on the broker could send it) |

- **Peer address only.** Only the socket's peer address counts (Kestrel's `RemoteIpAddress`; no forwarded-headers
  middleware), so `X-Forwarded-For` / `Forwarded` can't make a client local, and a browser here that opens this
  computer's LAN address is another device.
- **DNS rebinding and cross-site requests.** Any website open in a local browser can send requests to 127.0.0.1, so
  `LocalRequestGuard` (the first middleware) refuses loopback requests whose `Host` isn't `localhost`, a 127.x
  address or `[::1]` (any port), or whose `Origin`, when there is one, isn't such a host over http(s) (`null` origins
  included). It applies to the UI files, `POST /json-rpc` and the WebSocket handshake alike. Other devices may use
  any host name (`<pc>.local`, its address); they have to sign in anyway.
- **No factory password.** `AuthService` starts without one. `authorize newPassword` sets it without the current
  password from this computer (also how a forgotten one is replaced) and for the first password, while a device that
  signed in must give the current one. A HyperHDR password other than its factory default is imported and counts as
  set.
- **Remote access needs a password.** `setconfig` refuses `network.remoteAccess: true` without one
  (`ConfigCommand.PasswordNeeded`), and `RemoteAccess` keeps the network endpoints closed until one exists (a restored
  backup or an older database can't open them either). The UI's remote access dialog asks for the password (twice,
  with a strength hint) before it turns remote access on.
- **HyperHDR's access options are gone.** `apiAuth`, `localApiAuth`, `localAdminAuth`, `internetAccessAPI`,
  `restirctedInternetAccessAPI` and `ipWhitelist` are no longer in the `network` schema: imports drop them
  (`LegacyMigrations.RemoveAccessOptions`) and `setconfig` ignores them, so HyperHDR clients that send the whole
  section back keep working.
- **http only, port from the command line.** Backbloom serves http on the `--port` port (default 18290); there is no
  https listener. HyperHDR's `webConfig` section (its web server's port, https port, certificate, key and key password,
  web root) and `general.showOptHelp` were never read here, though 0.2.0 still stored them and showed the port and https
  fields in Settings → Remote access. They are gone from the schema: imports and restores drop them without a report
  (dropping them changes nothing), opening a database written by 0.2.0 deletes them from it (so a key password typed
  into those fields doesn't stay in the settings; backups made before still hold it), and `setconfig` ignores them
  (`LegacyMigrations.UnusedSettingsTypes` and `UnusedFields`). Encrypted access from other devices needs a certificate
  their browsers trust without a warning; the [roadmap](../roadmap.md#platform-and-distribution) plans it with the phone
  remote. Until then the turn-on dialog says that connections aren't encrypted.

## Lighting gate

`Backbloom.Engine/LightingGate` holds the output (`ComponentStates.SetHeld`): the controllers stay closed and nothing is
sent, while capture, mapping and the preview keep running and the protocol's switches keep the user's choice. It holds:

- **Until the first run is completed.** The meta key `first_run_completed` (`ISetupState`, in `settings.db`'s `meta`,
  not in a settings section, so backups, restores and imports don't touch it) is set by "Turn on lights" in the
  first-run setup or in the banner shown after "Look around first" (`setup start`). A new data directory and every
  first-start import wait for it; while it waits, the window opens even with `--no-window`. An update keeps the key, so
  it never holds a configured setup (or an autostarted one).
- **While HyperHDR (`hyperhdr.exe`) or Hyperion (`hyperiond.exe`) runs.** `OtherAppMonitor` asks `IProcessList` at
  start and every 2 s (`WindowsProcessList`: one Toolhelp snapshot, image names only, no handles; elsewhere
  `Process.GetProcessesByName`). When it quits, the output follows within 2 s; when it starts later, Backbloom switches
  its controllers off and lets go.

The gate starts before the engine, so a held start never opens a controller. The startup effect waits for the release
and runs then, as if the app had started at that moment.

## First-run setup

The first-run setup (UI, `ui/src/pages/welcome`) is shown instead of the app until `firstRunCompleted`. It has five
steps; What each light does appears only once there are lights.

1. **Welcome**: what Backbloom does. After an import, what came over from HyperHDR (the device, the number of LEDs, the
   screen sync, color and effect settings); on a new install, what to check before starting. While HyperHDR runs, two
   sentences say that Backbloom waits and takes over by itself when it quits. The technical summary (monitor, frame
   rate, startup effects, the imported database's path) shows only with expert tools on.
2. **Your lights**: the hub every board and set of room lights comes back to. It lists everything added so far (one row
   per device with its strips or lights; the test device of a new install, `file`, is not listed; no addresses unless
   expert tools are on) and offers Add more with the three ways of Add device (LED strips on a WLED board, Room lights,
   Another kind of device; see [docs/guided-setup.md](../guided-setup.md)). Each opens that flow of the same dialog as
   on Devices (`AddDeviceDialog`, with the network search and its explanation). With nothing added yet, it asks "What
   do you have?" with the same three. LED strips on a WLED board is "Set up a WLED board": one Search, the Wi-Fi for a
   new board, the power supply, finding the strips, each strip's type and checks. The first real device replaces the
   test device. Every strip and light starts with the screen's average (its route isn't known yet).
3. **What each light does**: the room as Layout draws it in its live colors (the preview runs while the output is
   held), the light being chosen picked out, and the monitor's size from `hardware screens` (the room's screen takes it
   while it still has the default size). A row per strip or light offers Screen average, Screen edge, or Its own effect
   or color (the layout's roles; the rest is on Layout); each choice is saved to the layout at once. Screen edge for a
   strip on a WLED board first traces it: the user draws its route from the board, the LEDs are spread along it, and
   the board lights those on the corners with `wled-test` `points` to check them. Skip — use the defaults puts the
   paths back as added.
4. **Feel and games**: Home's Look (the same control, saved at once), and optionally Add your game to Gaming. The picked
   running program goes into the default "Gaming" profile (Screen edges with the Intense Look; made again when it was
   deleted; saved with the layout). Skip — use the defaults sets Natural (on a new install).
5. **Turn on**: "Your lights stay as they are until you turn them on here", the unchecked Start with Windows box and
   Turn on lights. The layout is saved first (unless it has a problem that blocks saving), then `setup start` (while
   HyperHDR runs, it takes over once HyperHDR quits). The app then opens on Devices when there is still no real device,
   on Layout when the layout couldn't be saved, else on Home.

"Look around first" opens the app without turning the lights on: a banner says they are waiting and offers Turn on
lights, and Home shows "Waiting — turn on your lights to start" (or "Waiting for HyperHDR to quit").

## No lights yet

Until a real device exists, Home and Devices show "No lights yet" with one primary Add your lights (the same Add device
dialog) instead of controls that do nothing; that is also where "Look around first" and the setup's Skip lead. The test
device (`file`, colors go nowhere) is listed on Devices only with expert tools on, and its status is never "Sending
colors" in green ("Test device: the colors go nowhere").

## Network access

`Backbloom.App/RemoteAccess` follows the setting `network.remoteAccess` (default off, also for imports).

- **Off**: Kestrel listens on 127.0.0.1 only and nothing is advertised.
- **On**: an endpoint per IPv4 interface address (`<address>:<port>`) comes from a reloadable configuration
  (`NetworkEndpointConfiguration`, Kestrel `Configure(..., reloadOnChange: true)`), so endpoints are bound and unbound
  live and follow address changes (`NetworkChange.NetworkAddressChanged`). The loopback endpoint is configured in code
  and never touched, so the app's own window stays connected. Specific addresses are used rather than 0.0.0.0, so they
  never overlap the loopback endpoint and the tests (127.0.0.2 as the "network" address, `RemoteAccessOptions`)
  exercise the same binding. Unbinding waits for open connections on those endpoints to end (a few seconds). The mDNS
  responder starts and stops with them.
- **`--bind`** fixes one address instead, and the setting has no effect.

The UI (Settings → Remote access, with the password and app keys) explains what turning it on does (and that
Windows asks next, Private networks only) before it sets the setting. Without a password the endpoints stay closed even
when the setting is on (see [Access](#access-this-computer-and-other-devices)); setting one opens them at once.

## Protocol: `setup`

`setup` is a Backbloom addition:

| Subcommand | Params | Who |
|---|---|---|
| `getstate` | | any authorized session |
| `start` | `{ autostart? }` | admins |
| `autostart` | `{ enabled }` (the welcome, the tray menu and Settings → General → Start with Windows) | admins |
| `acknowledge` | `{ topic: "discovery" }` | admins |

Each replies with:

```text
{ firstRunCompleted, outputHeld, otherApp: { name, executable } | null, importedFrom,
  autostart: { available, enabled },
  remoteAccess: { enabled, fixedByCommandLine, port, urls, advertised }, passwordSet, acknowledged }
```

The `setup-update` event pushes the same object on every change (also when the password is set).
