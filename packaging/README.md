# Packaging (Windows)

Backbloom ships as a per-user installer made with [Velopack](https://velopack.io) (MIT): no administrator rights, a
Start menu shortcut, an entry in Apps & features, and automatic updates.

## Building the installer

```powershell
$env:DOTNET_ROOT = 'C:\Code\deps\dotnet'     # the development machine; CI uses actions/setup-dotnet
./packaging/build.ps1                          # -Dev, -Channel beta, -SignTemplate '...', -SkipUi, -Output <dir>
```

The script

1. builds the UI (`npm ci` when `node_modules` is missing, `npm run build`);
2. publishes `backend/src/Backbloom.App` self-contained for win-x64 with ReadyToRun into `artifacts/publish` (the UI as
   `ui/`, `LICENSE.txt`, `NOTICE.md`, `THIRD-PARTY-NOTICES.md` next to `backbloom.exe`);
3. packs it with `vpk` (pinned in `dotnet-tools.json`, restored with `dotnet tool restore`) into `artifacts/releases`:

| File | |
|---|---|
| `BackbloomApp-<channel>-Setup.exe` | the installer (about 81 MB) |
| `BackbloomApp-<version>-<channel>-full.nupkg` | the full release package (about 74 MB); later releases add delta packages |
| `BackbloomApp-<channel>-Portable.zip` | a portable copy |
| `releases.<channel>.json`, `RELEASES-<channel>`, `assets.<channel>.json` | the release feed |

The version is the `<Version>` in `backend/Directory.Build.props` (the only place it is set). Nothing is installed or
uploaded by the script.

**Versions.** The version changes only for a release (see
[Branches and releases](../docs/development.md#branches-and-releases)); a release commit is tagged `v<version>` and its
notes are its section in [CHANGELOG.md](../CHANGELOG.md). To try changes before a release, `-Dev` builds
`<next patch>-dev.<date>.<time>` (e.g.
`0.1.4-dev.20261020.103045` after 0.1.3; the time without leading zeros, e.g. `.2820` at 00:28:20, as SemVer requires
for numbers) without changing any file: each dev build installs over the previous one, and the 0.1.4 release over all
of them (SemVer orders pre-releases before the release, and compares the numbers as numbers).

## Install, first run, reinstall, uninstall

Velopack installs without a wizard: `Setup.exe` shows its splash screen, installs and starts Backbloom, so the app's
first window is the installer's "finish page".

1. **Install.** Into `%LOCALAPPDATA%\BackbloomApp` (`Update.exe`, `current\` with the app), with a Start menu shortcut
   and an entry in Apps & features; no administrator rights, nothing else is registered (no autostart, no firewall
   rule). The package id is `BackbloomApp` rather than `Backbloom` because Velopack deletes its whole folder on
   uninstall, and `%LOCALAPPDATA%\Backbloom` is the data directory, which must survive.
2. **First start.** HyperHDR's settings are imported (from `%LOCALAPPDATA%\HyperHDR`, only copied) and the window opens
   in front with the **welcome**: what was imported (LEDs, controllers with driver and address, captured monitor,
   effects), an unticked "Start Backbloom when I sign in to Windows" box and **Start lighting**. Nothing is sent to the
   LEDs and the startup effect doesn't run before that. While HyperHDR runs, the welcome says so (quit it from its
   tray icon; turn off its autostart under Windows Settings > Apps > Startup) and Start lighting waits until it has
   quit. The welcome comes back on every start until the lighting was started once (the window opens even when
   started with `--no-window`); the flag is kept in the data directory, so the installer doesn't need a first-run
   hook.
3. **Network.** The app listens on 127.0.0.1 only, so Windows doesn't ask about network access. Only when the user
   turns on Settings > Network > "Control from your phone / other devices" (after an explanation that Windows asks
   next and that Private networks are enough, and after choosing the password other devices sign in with) does it
   listen on the network and advertise itself with mDNS. The app's own window never asks for a password. Device
   discovery runs only on Find and uses short-lived ephemeral sockets.
4. **Reinstall.** Running `Setup.exe` again on a computer where Backbloom is installed: Velopack reports that it is
   already installed and offers to repair (reinstall) it. Either way only the program folder is replaced; settings,
   logs, the first-run flag and the window position in `%LOCALAPPDATA%\Backbloom` are kept, so a reinstall neither
   imports HyperHDR again nor shows the welcome again. To start over completely, uninstall, delete
   `%LOCALAPPDATA%\Backbloom` and install again.
5. **Update.** A newer release is applied at the next start (see below); settings and the first-run flag are kept,
   so the lights start as before unless HyperHDR runs.
6. **Uninstall** (Apps & features). It first asks the running app to quit (so the LEDs are switched off) and removes
   the "Start with Windows" entry; the data directory is kept. A firewall rule Windows created when the user allowed
   remote access stays until removed under Windows Security > Firewall & network protection > Allow an app through
   firewall.

## Updates

An installed copy (`InstalledLayout`: `sq.version` next to the program, `Update.exe` above it) checks its release feed
2 minutes after the start and then daily, downloads a newer release in the background, and Velopack installs it at the
next start, before the lights come on. A failed check is retried after an hour.

| Option | Environment | |
|---|---|---|
| `--update-url <url>` | `BACKBLOOM_UPDATE_URL` | release feed: the folder that holds `releases.<channel>.json` and the packages (https, http or file URL) |
| `--update-channel <name>` | `BACKBLOOM_UPDATE_CHANNEL` | channel to follow; default: the one the installed package was built for |

The built-in feed is the placeholder `UpdateSettings.PlaceholderFeedUrl` (`https://updates.backbloom.invalid/windows`),
which turns updates off until a real update server exists; set the real URL there once it does. To publish a release,
upload the contents of `artifacts/releases` to the feed folder (keep the older packages, Velopack builds deltas from
them: download the previous `releases.<channel>.json` and packages into `artifacts/releases` before packing).
Builds that aren't installed (development, publish folder, tests) never contact a feed.

Channels: `stable` by default; pack with `-Channel beta` for a beta feed. An installed copy follows its own channel
unless `--update-channel` says otherwise.

## Code signing (to do)

The package is unsigned today, so SmartScreen warns when the installer is downloaded. To sign, pass a SignTool
command with `{{file}}` for the file to sign; `vpk` signs every program and library of the package and `Setup.exe`:

```powershell
./packaging/build.ps1 -SignTemplate 'signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f C:\keys\backbloom.pfx /p <password> {{file}}'
```

(or `/sha1 <thumbprint>` for a certificate in the store, or an Azure Trusted Signing setup through `vpk`'s
`--azureTrustedSignFile`). Getting a code signing certificate is the open step.

## Verifying a package without installing

Installing writes to the user's profile (Start menu, uninstall entry, `%LOCALAPPDATA%`), so a test build is checked
without installing it:

- the package contents: `artifacts/releases/*-full.nupkg` is a zip; `lib/app/` must hold `backbloom.exe`, `ui/index.html`,
  `sq.version` and the license files;
- the app from the publish folder, on a test data directory and port:

  ```powershell
  artifacts/publish/backbloom.exe --service --port 19090 --data-dir C:\Code\deps\testdata\finalize --legacy-data-dir none --capture synthetic
  artifacts/publish/backbloom.exe --quit --data-dir C:\Code\deps\testdata\finalize
  ```

## Third-party notices

`Update-ThirdPartyNotices.ps1` regenerates `THIRD-PARTY-NOTICES.md` (summary and license texts) and
`3RD_PARTY_LICENSES` (shown on the UI's About page) from the app's NuGet packages and the UI's npm production
dependencies, and fails on a license outside the permissive allow list. `-Check` only verifies (CI).
`licenses/` holds the MIT template and the Apache 2.0 text used for packages that don't ship their license file.
