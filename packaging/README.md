# Packaging (Windows)

Overglow ships as a per-user installer made with [Velopack](https://velopack.io) (MIT): no administrator rights, a
Start menu shortcut, an entry in Apps & features, and automatic updates.

## Building the installer

```powershell
$env:DOTNET_ROOT = 'C:\Code\deps\dotnet'     # the development machine; CI uses actions/setup-dotnet
./packaging/build.ps1                          # -Channel beta, -SignTemplate '...', -SkipUi, -Output <dir>
```

The script

1. builds the UI (`npm ci` when `node_modules` is missing, `npm run build`);
2. publishes `backend/src/Overglow.App` self-contained for win-x64 with ReadyToRun into `artifacts/publish` (the UI as
   `ui/`, `LICENSE.txt`, `NOTICE.md`, `THIRD-PARTY-NOTICES.md` next to `overglow.exe`);
3. packs it with `vpk` (pinned in `dotnet-tools.json`, restored with `dotnet tool restore`) into `artifacts/releases`:

| File | |
|---|---|
| `OverglowApp-<channel>-Setup.exe` | the installer (about 81 MB) |
| `OverglowApp-<version>-<channel>-full.nupkg` | the full release package (about 74 MB); later releases add delta packages |
| `OverglowApp-<channel>-Portable.zip` | a portable copy |
| `releases.<channel>.json`, `RELEASES-<channel>`, `assets.<channel>.json` | the release feed |

The version is the `<Version>` in `backend/Directory.Build.props` (the only place it is set). Nothing is installed or
uploaded by the script.

## What the installer does

- Installs into `%LOCALAPPDATA%\OverglowApp` (`Update.exe`, `current\` with the app), adds the Start menu shortcut and
  the uninstall entry, and starts Overglow. The package id is `OverglowApp` rather than `Overglow` because Velopack
  deletes its whole folder on uninstall, and `%LOCALAPPDATA%\Overglow` is the data directory, which must survive.
- An installed copy listens on every interface (so phones can open the UI; Windows asks once whether to allow it) and
  imports HyperHDR's settings on the first start.
- Uninstalling first asks the running app to quit (so the LEDs are switched off) and removes the "Start with Windows"
  entry; the data directory is kept.

## Updates

An installed copy (`InstalledLayout`: `sq.version` next to the program, `Update.exe` above it) checks its release feed
2 minutes after the start and then daily, downloads a newer release in the background, and Velopack installs it at the
next start, before the lights come on. A failed check is retried after an hour.

| Option | Environment | |
|---|---|---|
| `--update-url <url>` | `OVERGLOW_UPDATE_URL` | release feed: the folder that holds `releases.<channel>.json` and the packages (https, http or file URL) |
| `--update-channel <name>` | `OVERGLOW_UPDATE_CHANNEL` | channel to follow; default: the one the installed package was built for |

The built-in feed is the placeholder `UpdateSettings.PlaceholderFeedUrl` (`https://updates.overglow.invalid/windows`),
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
./packaging/build.ps1 -SignTemplate 'signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f C:\keys\overglow.pfx /p <password> {{file}}'
```

(or `/sha1 <thumbprint>` for a certificate in the store, or an Azure Trusted Signing setup through `vpk`'s
`--azureTrustedSignFile`). Getting a code signing certificate is the open step.

## Verifying a package without installing

Installing writes to the user's profile (Start menu, uninstall entry, `%LOCALAPPDATA%`), so a test build is checked
without installing it:

- the package contents: `artifacts/releases/*-full.nupkg` is a zip; `lib/app/` must hold `overglow.exe`, `ui/index.html`,
  `sq.version` and the license files;
- the app from the publish folder, on a test data directory and port:

  ```powershell
  artifacts/publish/overglow.exe --service --port 19090 --data-dir C:\Code\deps\testdata\finalize --legacy-data-dir none --capture synthetic
  artifacts/publish/overglow.exe --quit --data-dir C:\Code\deps\testdata\finalize
  ```

## Third-party notices

`Update-ThirdPartyNotices.ps1` regenerates `THIRD-PARTY-NOTICES.md` (summary and license texts) and
`3RD_PARTY_LICENSES` (shown on the UI's About page) from the app's NuGet packages and the UI's npm production
dependencies, and fails on a license outside the permissive allow list. `-Check` only verifies (CI).
`licenses/` holds the MIT template and the Apache 2.0 text used for packages that don't ship their license file.
