# JSON-RPC protocol

The UI and other clients talk to the app with JSON messages, compatible with HyperHDR's JSON API for the commands the
UI uses (`ui/src/api/rpcApi.ts` is the reference client).

## Transports

| Transport | Where | Notes |
|---|---|---|
| WebSocket | any path on the web port (`ws://<host>:18290/`) | requests, replies and event pushes; one session per connection |
| HTTP | `POST /json-rpc` | one request (or an array) per call, no events |
| MQTT | `<topic>/JsonAPI` -> `<topic>/JsonAPI/response` | when the `mqtt` section enables it; runs with the rights of a local client |

The web port is 18290 by default (`--port`). An installed copy listens on every interface, other builds on
127.0.0.1 only (`--bind`).

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

Local clients need the admin password for configuration unless `network.localAdminAuth` is off; network clients
always do. `authorize` subcommands: `tokenRequired`, `adminRequired`, `newPasswordRequired`, `login` (password or
token), `logout`, `newPassword`, `createToken`, `getTokenList`, `deleteToken`. The factory password is `overglow`.
Passwords and tokens are stored as PBKDF2 hashes.

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

Events: `components-update`, `priorities-update`, `adjustment-update`, `settings-update`, `leds-update`,
`imageToLedMapping-update`, `performance-update`, `token-update`, `instance-update`, `sessions-update`,
`controllers-update`, `capture-update` (screen capture started or stopped, monitor HDR mode and tone mapping),
`logging-update`, and the LED color stream.

Differences from HyperHDR (rejected invalid settings, persisted `adjustment`, restore without restart, and others)
are listed in [backend/README.md](../backend/README.md#json-rpc-protocol).
