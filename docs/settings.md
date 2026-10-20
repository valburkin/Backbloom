# Settings and HyperHDR import

## Where settings live

The data directory holds everything that belongs to one user's setup:

| Path | |
|---|---|
| `settings.db` | SQLite: settings sections, controllers, credentials (hashes), instance id |
| `backups/` | settings saved before an import or a restore |
| `imported/` | copies of imported databases (only the copy is opened) |
| `logs/` | `overglow-yyyyMMdd.log`, a new file per day and per 10 MB, the newest 10 kept |
| `window.json`, `webview/` | window placement, the window's browser profile |
| `lut_lin_tables.3d` | HDR tone mapping table, created on first use |

Default location: `%LOCALAPPDATA%\Overglow` for an installed copy, `backend/.local/data` for a build run from the
source tree; `--data-dir` (or `OVERGLOW_DATA_DIR`) overrides it. One app runs per data directory. The installer keeps
the program in `%LOCALAPPDATA%\OverglowApp`, separate from the data, so uninstalling keeps the settings.

## Sections

`general`, `logger`, `device`, `color`, `smoothing`, `systemGrabber`, `blackborderdetector`, `foregroundEffect`,
`backgroundEffect`, `soundEffect`, `webConfig`, `effects`, `systemControl`, `network`, `ledConfig`, `leds`, `mqtt`,
`layout`. Their JSON schemas are in `backend/src/Overglow.Settings/Schemas` (the LED driver schemas in
`backend/src/Overglow.Devices/Schemas`); defaults exist only there.

- `setconfig` validates strictly: an invalid value is rejected with a per-field error and nothing is stored.
- The UI stores most changes on its own, 0.5 s after the last edit or when the page closes (Colors, Screen capture,
  Effects, Settings → General and Logs). `webConfig`, `network`, `mqtt` and the layout wait for Save, since a
  half-typed port or address would take effect.
- Imports and data written by an older schema version are repaired instead (numbers clamped, unknown fields dropped,
  invalid values reset to their default), and every repair is logged.
- Every change is one SQLite transaction; `PRAGMA user_version` is the schema version and forward migrations run once
  each. A database from a newer version is refused, never rewritten.

## Importing HyperHDR

On the first start (no first-start marker in `settings.db`) Overglow imports the first database it finds:
`<data>/db/overglow.db` (the earlier C++ Overglow build), then `<HyperHDR data>/db/hyperhdr.db` (HyperHDR's folder:
`%LOCALAPPDATA%\HyperHDR` or `~/.hyperhdr`; `--legacy-data-dir` overrides it, `none` turns it off; builds run from
the source tree don't look unless told). `overglow --import <file>` imports explicitly.

What the import does (idempotent, one log line per change, never logging secrets):

- reads HyperHDR v17 to v24-beta databases and the C++ Overglow format;
- HyperHDR instances become one pipeline: global sections and the first instance's per-instance sections become the
  pipeline's settings, the `device` of each instance becomes a controller;
- removed features are dropped (protobuf/flatbuffers/UDP/boblight/JSON servers, forwarder, the capture-card stack,
  CEC); removed effects map to the kept ones; a device type that doesn't exist becomes the `file` driver;
- HDR tone mapping on becomes `auto` (applied only while the captured monitor is in HDR mode; HyperHDR also applied
  it to SDR monitors, which oversaturates the colors), off stays `off`;
- the admin password and API tokens keep working (their HyperHDR hashes are replaced by PBKDF2 at the first sign-in);
  a password still at HyperHDR's factory default isn't imported (Overglow has no factory password; the user sets one
  when turning on remote access);
- HyperHDR's access options in `network` (`apiAuth`, `localApiAuth`, `localAdminAuth`, `internetAccessAPI`,
  `restirctedInternetAccessAPI`, `ipWhitelist`) are removed: this computer always has full access, other devices
  always sign in. Schema migration 4 removes them from databases of earlier Overglow versions, together with the
  factory password `overglow` those versions stored.

## First run

A new data directory, and every first-start import, starts with the LEDs held: nothing is sent to the LED controllers
and the startup effect waits until the user presses **Start lighting** in the welcome window (which opens on every
start until then, even from autostart). The flag lives in the database's metadata (`first_run_completed`), so a
backup restore or an import doesn't reset it. Databases from an earlier Overglow version count as started (schema
migration 3), so an update never leaves a configured setup dark.

Independently of the first run, the LEDs are held while HyperHDR (`hyperhdr.exe`) or Hyperion (`hyperiond.exe`) runs;
Overglow checks every 2 seconds and takes over when it has quit.

`network.remoteAccess` (default off, also for imports): off, the web UI only accepts connections from this computer;
on, it also listens on the computer's network addresses and announces itself with mDNS. It needs a password set by
the user (or imported from HyperHDR): the UI asks for one when remote access is turned on, and the listeners stay
closed without it. This computer never needs the password. Autostart is off unless the user turns it on (welcome
checkbox, tray menu).

Backups (`save-db`) use the `Overglow_export_format_v1` JSON format; `load-db` also accepts HyperHDR's v17/v20
exports. Credentials are never part of a backup.

Details, including the table layout and every migration rule: [backend/README.md](../backend/README.md#settings-storage-and-hyperhdr-import).
