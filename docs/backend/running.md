# Running the backend

How to start `backbloom.exe`, its command-line options, and how the web UI is served. For the everyday build and test
commands, see the [development guide](../development.md).

## Contents

- [Command-line options](#command-line-options)
- [Console behavior](#console-behavior)
- [Serving the UI](#serving-the-ui)

## Command-line options

```powershell
cd backend
dotnet run --project src/Backbloom.App -c Release -- --port 18290
```

The settings options can also be given as `BACKBLOOM_<NAME>` environment variables. Unknown options and invalid values
are errors (exit code 2, with the usage). `--name value` and `--name=value` both work.

| Option | Default | Description |
|---|---|---|
| `--port` | `18290` | Web port (HTTP, WebSocket JSON-RPC, UI files). HyperHDR keeps 8090/8092. |
| `--bind` | `127.0.0.1`, plus every interface address while `network.remoteAccess` is on | The one address to listen on; it overrides the remote access setting (advanced use). Without it every copy, installed or not, listens on loopback only until the user turns remote access on (see [Network access](access-and-first-run.md#network-access)), so nothing makes Windows ask for network access unannounced. |
| `--data-dir` | per-user `Backbloom` folder; `backend/.local/data` when run from the source tree | `settings.db` (settings, controllers, credentials as hashes, instance id), `backups/`, `imported/`, `logs/`, `window.json`, `webview/` (the window's browser profile). See [Settings storage](settings-storage.md). |
| `--legacy-data-dir` | HyperHDR's data folder; none when run from the source tree | Where the first start looks for `db/hyperhdr.db` to import. `none` turns it off. |
| `--ui-dir` | `ui/` next to the program, else `ui/dist` of the source tree | Built React UI to serve. |
| `--capture` | `screen` | Frame source: `screen` (DXGI desktop duplication, configured by `systemGrabber`; falls back to the test pattern on other operating systems) or `synthetic` (test pattern, used by the server tests). |
| `--capture-image` | none | A still picture (binary PPM, P6) as the frame source instead (it overrides `--capture`); scaled down to at most 512 px wide. For screenshots and demos with known colors. |
| `--fps` | `60` | Rate of the synthetic test source or the still picture. |
| `--lut-dirs` | data dir, app dir | Directories searched for `lut_lin_tables.3d` (or `.3d.zst`), separated by `;`; the default table is written to the first. |
| `--no-window` | | Start in the tray without opening the window (the autostart entry uses it). |
| `--service` | | No tray icon, no window, console log: headless use (HyperHDR's `--service`). Power events are still handled. |
| `--update-url` | the GitHub releases | Where an installed copy looks for updates: a GitHub repository or a release feed URL, `none` for off (see [packaging/README.md](../../packaging/README.md#updates)). |
| `--update-channel` | the installed package's | Release channel to follow (`stable`, `beta`, ...). |
| `--import <file>` | | Import a HyperHDR database into the data directory and exit. |
| `--quit` | | Ask the instance running with the data directory to quit; waits until it is gone (installers, scripts). |
| `--version`, `-h`/`--help` | | Print and exit. |

## Console behavior

`backbloom.exe` is a GUI program: it opens no console window when started from Explorer or autostart. Started from a
terminal, it attaches to that console, so the usage, `--version` and the log of `--service` show there. Ctrl+C stops it
gracefully.

## Serving the UI

Two ways to use the UI with a running backend:

- **Served by the backend**: `cd ui && npm ci && npm run build`, then build the backend (the UI is copied next to the
  program as `ui/`) and open `http://127.0.0.1:18290`, or use the app's window.
- **From a dev or preview server**: `npx vite preview --port 4730` in `ui/` and open
  `http://localhost:4730/?backend=localhost:18290`.

The UI finds its backend at the page's own host (`ui/src/api/backend.ts`: `?backend=` / `VITE_BACKEND` first), so it
needs no change when the app serves it.

`UiFiles` serves `index.html` for `/` and for any other GET without a file extension (single-page fallback; the UI
routes by URL hash). It caches the content-hashed `assets/` for a year (`immutable`) and revalidates everything else
(`no-cache` with ETag), so a new version shows up at once. Missing files with an extension stay 404. After rebuilding
the UI, rebuild the backend (or pass `--ui-dir ../ui/dist`) to serve it.

The UI from this computer (the app's window, a browser here) never asks for a password; other devices sign in (see
[Access](access-and-first-run.md#access-this-computer-and-other-devices)).
