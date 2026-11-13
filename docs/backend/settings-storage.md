# Settings storage and HyperHDR import

How settings are validated and stored in SQLite, how HyperHDR's per-instance settings map onto Backbloom's single
pipeline, every import rule, the first start and backups. [docs/settings.md](../settings.md) is the user-oriented
overview of the same data.

## Contents

- [Sections and validation](#sections-and-validation)
- [Database](#database)
- [Instances become one pipeline](#instances-become-one-pipeline)
- [Import](#import)
- [First start and data directory](#first-start-and-data-directory)
- [Backups](#backups)

## Sections and validation

Every settings type HyperHDR's C++ code kept is a section: `general`, `logger`, `device`, `color`, `smoothing`,
`systemGrabber`, `blackborderdetector`, `foregroundEffect`, `backgroundEffect`, `soundEffect`, `webConfig`, `effects`,
`systemControl`, `network`, `ledConfig`, `leds`, `mqtt`, `layout`, plus Backbloom's own `presets` (the profile chosen by
hand, see [Profiles](layout-and-controllers.md#profiles-the-apps-presets)).

- **Schemas.** Their schemas are HyperHDR's files (`sources/base/schema`, copied into `Backbloom.Settings/Schemas`). The
  additions are `mqtt.homeAssistantDiscovery`, `network.remoteAccess` and `presets.json`. Sections the store doesn't
  know, such as an earlier build's `modes` and `game`, are ignored when it loads. The LED driver schemas come from
  `Backbloom.Devices`, and defaults exist only in the schemas.
- **Validator.** The in-house validator stays, because the schemas use the draft-03 `required: true` form that the
  standard validators don't support. `setconfig` uses it strictly (reject, per-field errors). Only imports and stored
  data from an older schema use its repair mode: clamp numbers, reset values of the wrong type or outside an enum to
  their default, drop unknown fields, report every fix.

## Database

`settings.db` (Microsoft.Data.Sqlite) has one connection per process, and every use is serialized. Reads come from a
validated in-memory copy; every update is one transaction written before the copy changes.

`PRAGMA application_id` marks the file, and `PRAGMA user_version` is the schema version. `SettingsDatabase.Migrations`
lists the forward steps, each applied once in its own transaction; a file from a newer version is refused, never
rewritten.

Schema 1:

| Table | Holds |
|---|---|
| `settings (section, value, updated_at)` | the pipeline's sections (JSON text) |
| `controllers (id, name, enabled)`, `controller_settings (controller_id, section, value, updated_at)` | the LED controllers and their `device` |
| `admin_password`, `api_tokens` | hashes with salt, algorithm and iterations |
| `meta (key, value)` | instance id, first-start and import markers |

Rows of sections this version doesn't know (written by a newer one) are kept untouched.

## Instances become one pipeline

HyperHDR ran a complete pipeline per instance; Backbloom runs one pipeline that feeds many controllers:

| HyperHDR | Backbloom |
|---|---|
| global types (`general`, `logger`, `network`, `webConfig`, `mqtt`, `systemGrabber`, `soundEffect`, `layout`) | pipeline sections |
| per-instance types of the first instance (`leds`, `ledConfig`, `color`, `smoothing`, `blackborderdetector`, `foregroundEffect`, `backgroundEffect`, `effects`, `systemControl`) | pipeline sections |
| `device` of instance N (in instance order), name and enabled flag from `instances` | controller N; controller 0 is the `device` of `getconfig`/`setconfig` (with a `layout`, the table mirrors its controllers) |
| per-instance settings of further instances | not imported (reported) |

Without a `layout`, the engine drives controller 0 (`device` + `leds`); a saved layout describes every controller (see
[LED controllers and the layout](layout-and-controllers.md)).

## Import

`SettingsImporter` and `LegacyMigrations` are ported from HyperHDR's InstanceConfig, SettingsMigration, LegacyMigration
and AccessManager/AuthTable. They read HyperHDR v17 to 24-beta databases (`hyperhdr_instance` column, user `Hyperhdr`);
the source is copied to `imported/` and only the copy is opened.

The rules are all idempotent and logged one line each (never with secrets):

- format upgrades by `general.version` (HyperHDR's upgradeDB up to 6: backlight threshold steps, pause while locked);
- removed settings types are dropped: proto/flatbuffers/raw UDP/boblight/JSON servers, forwarder, the capture-card
  stack (`videoGrabber`, `videoControl`, `videoDetection`, `automaticToneMapping`), `grabberV4L2`; unknown types too;
- CEC (`systemControl.cecControl`) and signal detection options (`systemGrabber`) are removed;
- removed effects map to the kept ones (for example "Knight rider" -> "Breath", music color variants -> MULTI COLOR);
- a device type that Backbloom doesn't have becomes the `file` driver (no output), keeping color order and refresh
  time; the copy in `imported/` keeps the original for drivers ported later;
- HyperHDR's factory name "My HyperHDR Config" becomes "My Backbloom Config"; an empty `layout` is left absent;
- `systemGrabber.hdrToneMapping` true becomes `auto`, false `off` (see [Screen capture](screen-capture.md#capture-stages));
- `soundEffect.device`, a HyperHDR recording device name, becomes `default` (what this PC plays);
- every section is then repaired against its schema;
- credentials: the admin password hash (salted SHA-512) and API token hashes are imported as `LegacySha512` and keep
  working; `AuthService` replaces them with PBKDF2 on first use. A password still at HyperHDR's (or an earlier C++
  build's) factory default isn't imported: Backbloom has no factory password, and the user sets one for remote access;
- HyperHDR's `network` access options (`apiAuth`, `localApiAuth`, `localAdminAuth`, internet access, IP whitelist) are
  removed.

Converting is a pure function of the source, so importing the same file again changes nothing.

## First start and data directory

- **Data directory.** It defaults to the per-user `Backbloom` folder (`%LOCALAPPDATA%\Backbloom`,
  `~/Library/Application Support/Backbloom`, `$XDG_CONFIG_HOME/Backbloom`), or `backend/.local/data` for a build that
  runs from the source tree.
- **Automatic import.** When `settings.db` has no first-start marker, `<legacy>/db/hyperhdr.db` is imported
  (`--legacy-data-dir`, default HyperHDR's folder: `~/.hyperhdr` if it exists, else `%LOCALAPPDATA%\HyperHDR` /
  `~/Library/Preferences/HyperHDR` / `~/.config/HyperHDR`; source-tree builds don't look unless told). A failed import
  is logged and the app starts with defaults; it isn't retried automatically. Either way the lights wait for the
  first-run setup (see [Lighting gate](access-and-first-run.md#lighting-gate)); the imported file is remembered
  (`imported_from`) for its summary.
- **Explicit import.** `backbloom --import <file> --data-dir <dir>` imports a database explicitly (the current settings
  go to `backups/` first).

## Backups

`save-db` returns the export format of the C++ code (`Backbloom_export_format_v1`: `instances` + `settings` rows with
JSON text, per-instance types under instance 0, each controller's `device` under its id).
`load-db` also accepts `HyperHDR_export_format_v17`/`v20`. Credentials are never part of a backup.
