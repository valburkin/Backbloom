# Settings and HyperHDR import

## Where settings live

The data directory holds everything that belongs to one user's setup:

| Path | |
|---|---|
| `settings.db` | SQLite: settings sections, controllers, credentials (hashes), instance id |
| `backups/` | settings saved before an import or a restore |
| `imported/` | copies of imported databases (only the copy is opened) |
| `logs/` | `backbloom-yyyyMMdd.log`, a new file per day and per 10 MB, the newest 10 kept; a failed start or a crash is recorded there as a `FATAL` entry (and shown in a message box). `performance-yyyyMMdd.jsonl`: a start line per app start and a performance sample every 10 s, the newest 14 files (days the app ran) kept; `crash-yyyyMMdd-HHmmss.jsonl`: the start line and the last 30 minutes of samples before a crash, the newest 10 kept (format: [backend/README.md](../backend/README.md#performance-recording)) |
| `window.json`, `webview/` | window placement, the window's browser profile |
| `lut_lin_tables.3d` | HDR tone mapping table, created on first use |

Default location: `%LOCALAPPDATA%\Backbloom` for an installed copy, `backend/.local/data` for a build run from the
source tree; `--data-dir` (or `BACKBLOOM_DATA_DIR`) overrides it. One app runs per data directory. The installer keeps
the program in `%LOCALAPPDATA%\BackbloomApp`, separate from the data, so uninstalling keeps the settings.

## Sections

`general`, `logger`, `device`, `color`, `smoothing`, `systemGrabber`, `blackborderdetector`, `foregroundEffect`,
`backgroundEffect`, `soundEffect`, `webConfig`, `effects`, `systemControl`, `network`, `ledConfig`, `leds`, `mqtt`,
`layout`. Their JSON schemas are in `backend/src/Backbloom.Settings/Schemas` (the LED driver schemas in
`backend/src/Backbloom.Devices/Schemas`); defaults exist only there.

- `setconfig` validates strictly: an invalid value is rejected with a per-field error and nothing is stored.
- The UI stores most changes on its own, 0.5 s after the last edit or when the page closes (Colors, Screen sync,
  Effects, Settings → General and Support). `webConfig`, `mqtt` and the layout wait for Save, since a half-typed port
  or address would take effect; `network.remoteAccess` is stored when it is turned on or off.
- Each property's `access` is its tier in the UI (none: always shown, `"advanced"`: under More options, `"expert"`:
  only with Settings → General → Show expert and diagnostic tools, or while its value isn't the default); see
  [ui-guidelines.md](ui-guidelines.md).
- The UI's Settings page: General (`general`, Start with Windows, backups), Remote access (`network`, the password,
  app keys, `webConfig`), Integrations (`mqtt`), About, and Support (live log, `logger`) with expert tools on.
  `logger.level` defaults to `warn` (warnings and errors).
- Imports and data written by an older schema version are repaired instead (numbers clamped, unknown fields dropped,
  invalid values reset to their default), and every repair is logged.
- Every change is one SQLite transaction; `PRAGMA user_version` is the schema version and forward migrations run once
  each. A database from a newer version is refused, never rewritten.

### Screen sync, effects and sound

- `systemGrabber`: `fps` defaults to 30. `monitor_nits_auto` (default on, a Backbloom addition) takes the SDR white
  level of a monitor in HDR mode from Windows ("SDR content brightness"), read when the capture starts and watched
  while it runs; `monitor_nits` is the level set by hand, used with the switch off or when Windows reports none.
- `systemControl` holds only `systemInstancePriority`, the priority of the screen. Screen sync itself is switched by
  the `SYSTEMGRABBER` component (Home → Show → Screen sync); HyperHDR's `systemInstanceEnable` did nothing here and is
  gone.
- `foregroundEffect` (the startup effect) defaults to "Breath", a short white fade, for 3 s; `backgroundEffect` (the
  idle effect, shown when nothing else is) is off and defaults to "Warm mood blobs".
- `soundEffect.enable` is "Listen to sound": music effects run only with it on (the `effect` command answers "Music
  needs sound input, which is off."), and the UI turns it on when a music effect is picked.

### The layout

`layout` describes where the lights are, as a schematic room (sizes in meters):

- `room`: `width`, `depth`, `height`; the `screen` on the front wall (top-left corner and size, in front-wall
  coordinates); `furniture` (`desk`, `tv` or `none`, drawn only).
- `controllers`: each with its outputs (`index`, `start`, `length`) and its LED driver settings (`device`).
- `strips`: each wired to one output of a controller, with its `ledCount` and a `path` of two or more points on the
  walls, the ceiling or the floor (from its first LED to its last; a point matched to the real lights carries its LED
  position `at`; "bends" and "matched" in the UI). Optional: `dark` LED ranges ("turned-off LEDs"), `depth`, `blend`
  ("softness") and `brightness`.
- The points split a strip into sections ("parts" in the UI); a point's `next` says what the section to the following
  point does: `{ kind: "edge", from, to }` repeats a stretch of the screen's outline (positions 0..4 clockwise from the
  top-left corner, one per edge; "Screen edge"), `whole` shows the whole picture ("Screen average"; its color changes more slowly than
  the smoothing settings say, about half a second more, so small changes in the picture don't make it flicker),
  `{ kind: "area", area }` one area of it, `none` nothing, and `gap` means the strip has no LEDs there (a cut corner, a
  cable). Without `next` a section repeats the stretch of the outline it faces when the room is seen straight on
  against the front wall.
- `{ kind: "effect", effect }` (an effect by its name, music effects included) and `{ kind: "color", color: [r, g, b] }`
  make a section play something of its own, whatever the rest of the lights show: its LEDs leave the screen mapping
  and form a zone with every other section of the same role, across strips and controllers. A zone's effect runs as
  if the zone were the screen (each LED where `auto` would put it), so it flows across strips; it goes through the
  color settings like any effect, a color zone doesn't (like a static color), except the brightness and the power
  limit. Zones keep the lights on when nothing else shows (the other LEDs are black then), and pause while the lights
  are off or held. An effect that doesn't exist leaves its zone dark.
- `profiles` (optional): other roles for the sections while a program runs, e.g.
  `{ id: "witcher", name: "Witcher 3", apps: ["witcher3.exe"], sections: { "ceiling": [{ kind: "color", color: [0, 40, 80] }, null] } }`:
  per strip id, one entry per section (`null` = auto); strips not listed keep the layout's sections, gaps can't be
  changed, and a list made for another number of sections is ignored. While one of a profile's programs runs (checked
  every 2 seconds, names compared without regard to case), its roles apply; the first profile in the list wins when
  programs of several run. Switching saves nothing and doesn't reconnect the controllers. The UI can also turn one on
  by hand to try it (Preview, `profile select`).

The LEDs are spread evenly over the sections that have LEDs (gaps count no length), so strips anywhere in the room get
sensible colors. The backend compiles the layout into the `leds` sampling areas (one per LED, all controllers one after
the other) whenever it is saved; `leds` sent along with it is replaced. An invalid layout is rejected with the path of
the problem (e.g. `layout.strips.1.path.0: is outside the room (the ceiling is 4.2 x 3.6 m)`). Details:
[backend/README.md](../backend/README.md#led-controllers-and-the-layout-port-step-6b).

## Importing HyperHDR

On the first start (no first-start marker in `settings.db`) Backbloom imports HyperHDR's database
`<HyperHDR data>/db/hyperhdr.db` (HyperHDR's folder:
`%LOCALAPPDATA%\HyperHDR` or `~/.hyperhdr`; `--legacy-data-dir` overrides it, `none` turns it off; builds run from
the source tree don't look unless told). `backbloom --import <file>` imports explicitly.

What the import does (idempotent, one log line per change, never logging secrets):

- reads HyperHDR v17 to v24-beta databases and the C++ Backbloom format;
- HyperHDR instances become one pipeline: global sections and the first instance's per-instance sections become the
  pipeline's settings, the `device` of each instance becomes a controller;
- removed features are dropped (protobuf/flatbuffers/UDP/boblight/JSON servers, forwarder, the capture-card stack,
  CEC, `systemControl.systemInstanceEnable`); removed effects map to the kept ones; a device type that doesn't exist becomes the `file` driver;
- HDR tone mapping on becomes `auto` (applied only while the captured monitor is in HDR mode; HyperHDR also applied
  it to SDR monitors, which oversaturates the colors), off stays `off`;
- the admin password and API tokens keep working (their HyperHDR hashes are replaced by PBKDF2 at the first sign-in);
  a password still at HyperHDR's factory default isn't imported (Backbloom has no factory password; the user sets one
  when turning on remote access);
- HyperHDR's access options in `network` (`apiAuth`, `localApiAuth`, `localAdminAuth`, `internetAccessAPI`,
  `restirctedInternetAccessAPI`, `ipWhitelist`) are removed: this computer always has full access, other devices
  always sign in. Imports drop them.

## First run

A new data directory, and every first-start import, starts with the LEDs held: nothing is sent to the LED controllers
and the startup effect waits until the user presses **Turn on lights** at the end of the first-run setup (which
opens on every start until then, even from autostart), or in the banner after "Look around first". The flag lives in the database's metadata (`first_run_completed`), so a
backup restore or an import doesn't reset it.

Independently of the first run, the LEDs are held while HyperHDR (`hyperhdr.exe`) or Hyperion (`hyperiond.exe`) runs;
Backbloom checks every 2 seconds and takes over when it has quit.

`network.remoteAccess` (default off, also for imports): off, the web UI only accepts connections from this computer;
on, it also listens on the computer's network addresses and announces itself with mDNS. It needs a password set by
the user (or imported from HyperHDR): the UI asks for one when remote access is turned on, and the listeners stay
closed without it. This computer never needs the password. Autostart is off unless the user turns it on (setup
checkbox, tray menu, Settings → General → Start with Windows).

Backups (`save-db`) use the `Backbloom_export_format_v1` JSON format; `load-db` also accepts HyperHDR's v17/v20
exports. Credentials are never part of a backup.

Details, including the table layout and every migration rule: [backend/README.md](../backend/README.md#settings-storage-and-hyperhdr-import).
