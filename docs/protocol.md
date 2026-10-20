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
setting on (Settings > Network) it also listens on the computer's network addresses. `--bind` fixes one address.

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

`tan` is echoed so clients can match replies. Requests of one connection run in order.

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
| `effect` | run an effect by name at a priority, optionally for a duration |
| `adjustment` | change color adjustment (stored) |
| `ledcolors` `ledstream-start` / `ledstream-stop` | live LED colors for the preview |
| `leddevice` `discover` / `getProperties` / `identify` | driver tools |
| `logging` `start` / `stop` | log history and live log lines |
| `performance-counters` | CPU, memory, frame and LED rates |
| `instance` `switchTo` | kept for HyperHDR clients |
| `setup` `getstate` / `start` / `autostart` / `acknowledge` | first-run welcome: whether the lighting was started once, HyperHDR running, what was imported, autostart, remote access addresses, whether a password is set (`passwordSet`); `start` ends the first-run hold (admin) |

Events: `components-update`, `priorities-update`, `adjustment-update`, `settings-update`, `leds-update`,
`imageToLedMapping-update`, `performance-update`, `token-update`, `instance-update`, `sessions-update`,
`controllers-update`, `capture-update` (screen capture started or stopped, monitor HDR mode and tone mapping),
`setup-update` (the `setup` state), `logging-update`, and the LED color stream.

Differences from HyperHDR (rejected invalid settings, persisted `adjustment`, restore without restart, and others)
are listed in [backend/README.md](../backend/README.md#json-rpc-protocol).
