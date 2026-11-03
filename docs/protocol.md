# JSON-RPC protocol

The UI and other clients talk to the app with JSON messages, compatible with HyperHDR's JSON API for the commands the
UI uses (`ui/src/api/rpcApi.ts` is the reference client).

## Transports

| Transport | Where | Notes |
|---|---|---|
| WebSocket | any path on the web port (`ws://<host>:18290/`) | requests, replies and event pushes; one session per connection |
| HTTP | `POST /json-rpc` | one request (or an array) per call, no events |
| MQTT | `<topic>/JsonAPI` -> `<topic>/JsonAPI/response` | when the `mqtt` section enables it; API rights without configuration access |

The web port is 18290 by default (`--port`). The app listens on 127.0.0.1 only; with the `network.remoteAccess`
setting on (Settings → Remote access) it also listens on the computer's network addresses. `--bind` fixes one address.

## Messages

```jsonc
// request
{ "command": "componentstate", "componentstate": { "component": "SMOOTHING", "state": false }, "tan": 7 }
// reply
{ "command": "componentstate", "tan": 7, "success": true, "info": { ... } }
// failure
{ "command": "componentstate", "tan": 7, "success": false, "error": "..." }
// event (after a subscription)
{ "command": "components-update", "data": { ... } }
```

`tan` is echoed so clients can match replies. Requests of one connection run in order, except the `hardware`
subcommands that talk to a board: over a WebSocket they reply when the board is done and the next requests don't wait.

## Authorization

This computer always has full access without a password; other devices (only possible with remote access on) always
sign in with the password or a token. "This computer" is decided by the connection's peer address alone: 127.0.0.0/8,
`::1` and IPv4-mapped `::ffff:127.x`. Headers such as `X-Forwarded-For` are ignored, and a browser on this computer
that opens its network address counts as another device.

A website open in a browser on this computer can reach 127.0.0.1 too, so loopback requests (HTTP and the WebSocket
handshake) must name a loopback `Host` (`localhost`, `127.x.x.x`, `[::1]`, any port) and, when a browser sends one, a
loopback `Origin`; others get 403 (DNS rebinding, cross-site requests). Programs that send no `Origin` are fine.

There is no factory password. Until the user sets one, other devices are refused (403), remote access can't be turned
on (`setconfig` fails with "Set a password before turning on remote access...") and the network listeners stay
closed. `authorize` subcommands: `tokenRequired`, `adminRequired`, `newPasswordRequired` (true while no password is
set), `login` (password or token), `logout`, `newPassword` (`{ newPassword, password? }`: the current `password` is
needed only from another device; this computer can set a new one without it, also when it was forgotten),
`createToken`, `getTokenList`, `deleteToken`. Passwords and tokens are stored as PBKDF2 hashes. HyperHDR's
`network.apiAuth`, `localApiAuth`, `localAdminAuth`, `internetAccessAPI`, `restirctedInternetAccessAPI` and
`ipWhitelist` no longer exist; `setconfig` ignores them.

## Commands

| Command | Purpose |
|---|---|
| `sysinfo`, `serverinfo` | system and app information; `serverinfo` with `subscribe` registers for events |
| `config` `getschema` / `getconfig` / `setconfig` | settings schemas and values; `setconfig` validates strictly and replies with `errors: [{path, message}]` instead of storing invalid values |
| `save-db` / `load-db` | backup and restore (also HyperHDR backups); restore applies while running |
| `componentstate` | switch `ALL`, `SMOOTHING`, `BLACKBORDER`, `SYSTEMGRABBER`, `LEDDEVICE` |
| `color`, `clear`, `clearall`, `sourceselect` | static colors and source selection by priority |
| `effect` | run an effect by name at a priority, optionally for a duration; a music effect is refused while sound capture is off (`serverinfo.sound.available`) |
| `effects` `previews` | a few looping seconds of one effect (`name`) or of every effect on the user's LEDs, for the effect cards: `{ fps, ledCount, standard, previews: [{ name, frames }] }`, `frames` the RGB bytes of each frame in base64 in the order of `leds`; `standard`: there are no LEDs yet, so a standard 16:9 frame was used (rendered on simulated time, not on the lights) |
| `adjustment` | change color adjustment (stored) |
| `ledcolors` `ledstream-start` / `ledstream-stop` | live LED colors for the preview |
| `leddevice` `discover` / `getProperties` / `identify` | driver tools; `identify` with `ledDeviceType: "blink"` finds an LED (see below) |
| `logging` `start` / `stop` | log history and live log lines |
| `performance-counters` | CPU, memory, frame and LED rates |
| `instance` `switchTo` | kept for HyperHDR clients |
| `setup` `getstate` / `start` / `autostart` / `acknowledge` | first-run setup: whether the lights were turned on once, HyperHDR running, what was imported, autostart, remote access addresses, whether a password is set (`passwordSet`); `start` ends the first-run hold (admin) |
| `profile` `getstate` / `select` / `programs` | the layout's profiles at runtime (see below) |
| `mode` `getstate` / `set` | Ambient or Game mode (see below) |
| `hardware` `find` / `connect-wifi` / `wled-identify` / `wled-probe-start` / `wled-probe-end` / `screens` / `usb-scan` / `usb-networks` / `usb-wifi` / `wled-read` / `wled-write` / `wled-wifi` / `wled-test` | admins: the guided hardware setup (one search for boards on USB, the network and their hotspots, putting a board on Wi-Fi with `hardware-progress-update` events, monitor sizes, Improv Wi-Fi over USB, setting up a WLED board and its checks); see [guided-setup.md](guided-setup.md#protocol-hardware) |

### Finding an LED (`leddevice identify`, `ledDeviceType: "blink"`)

Every other LED of the controller goes black while the chosen one shows red, green and blue for 1.5 s each.

| `params` | |
|---|---|
| `blinkIndex` | the LED: counted from the first LED of `controller`, or without one over all controllers' LEDs |
| `controller` | optional: the controller's id (or its position, a number) |
| `hold` | optional: `true` keeps the LED lit, cycling red, green and blue every 1.5 s, until the next blink request (any LED, any controller), a stop request, or 120 s; without it the blink ends after 4.5 s |
| `stop` | `true`: ends the blink (on `controller`, or on every controller); `blinkIndex` isn't needed |

A blink replaces the running one (also on another controller) and starts over at red. It also shows while the lights
are switched off or nothing is visible (the controller opens for it and closes again after it); switching `ALL` or
`LEDDEVICE` off ends it. Replies `{ controller, blinkIndex, hold }`, for a stop `{ controller, stopped: true }`. Errors:
an LED or controller that doesn't exist, a controller that isn't running (disabled, no usable driver settings), and
the output being held (before the first-run setup is completed, or while HyperHDR runs: nothing is sent then).

### Profiles (`profile`)

A layout's `profiles` give its strips other roles while a program runs; the first profile in the list one of whose
programs runs is active (checked every 2 s). `serverinfo.profile` is the active one, `{ id, name, manual, game }`, or
null (the layout as saved); `manual` is true for a profile chosen with `select`, `game` for a profile marked as a game's
(`game: true` in the layout: Game mode is on while it runs, see below).

| Subcommand | Params | Reply |
|---|---|---|
| `getstate` | | `{ profile }` |
| `select` | `{ id: string \| null }` | runs that profile whatever programs run, to try it out, until `select` with `id` null (or without it) goes back to choosing by the running programs; nothing is saved, and removing the profile from the layout or a restart goes back too. Replies `{ profile }`; an id the layout doesn't have is an error |
| `programs` | | admins: the programs with a window open, for picking a profile's programs: `{ programs: [{ exe, title }] }`, one per executable (`exe` is what `apps` holds, e.g. `witcher3.exe`), sorted by it |

`profile-update` pushes `{ profile }` whenever the active profile changes.

### Modes (`mode`)

Ambient (the lights as the settings say) or Game: while Game mode is on, only the screen is shown (colors, pictures,
effects and music stay in the priority list, active but not visible, and show again in Ambient), and the values of the
`game` section replace the transition time and spring (`smoothing.time_ms`, `stiffness`, `damping`, with smoothing on),
the saturation and color brightness gains (`color.channelAdjustment[0]`, whose minimum glow `backlightThreshold` is 0)
and raise the capture rate to at least
`game.fps`, without changing those sections. The mode chosen in the app is saved as `modes.mode`; a game's profile turns Game mode on while it runs.
`serverinfo.mode` is `{ mode, saved, gameProfile, dismissedGame }`: `mode` the one that applies, `saved` the chosen
one (both `"ambient"` or `"game"`), `gameProfile` the name of the game's profile that turned Game mode on, else null,
and `dismissedGame` the name of the running game whose Game mode was turned off with `set ambient`, else null.

| Subcommand | Params | Reply |
|---|---|---|
| `getstate` | | `{ mode }` |
| `set` | `{ mode: "ambient" \| "game" }` | saves the chosen mode; `ambient` while a game's profile has Game mode on also turns it off until another profile (or none) runs, and `game` while that game still runs only turns it back on (the saved mode stays). Replies `{ mode }`. API access is enough |

`mode-update` pushes `{ mode }` whenever it changes, in the order of the changes. Game mode's Intensity is set with `setconfig` on `game`.

Events: `components-update`, `priorities-update`, `adjustment-update`, `settings-update`, `leds-update`,
`imageToLedMapping-update`, `performance-update`, `token-update`, `instance-update`, `sessions-update`,
`controllers-update`, `capture-update` (screen capture started or stopped, monitor HDR mode and tone mapping),
`setup-update` (the `setup` state), `profile-update` (the active profile), `mode-update` (Ambient or Game), `hardware-progress-update` (the steps of `hardware connect-wifi`, to the asking connection only), `sound-update` (`serverinfo.sound` when the
sound capture starts, stops, fails or recovers), `logging-update`, and the LED color stream.

Differences from HyperHDR (rejected invalid settings, persisted `adjustment`, restore without restart, and others)
are listed in [backend/README.md](../backend/README.md#json-rpc-protocol).
