# Building Overglow on Windows (from source)

Verified recipe on Windows 11 x64 with Visual Studio 2026 (18.x) Community, CMake 4.3, Python 3.12.
All third-party SDKs go to `C:\Code\deps` (outside the repository); the build goes to `<repo>\build` (git-ignored),
where `<repo>` is the repository root. All commands are PowerShell, run from any directory unless stated otherwise.

## 1. Prerequisites

- Visual Studio 2026 (or 2022) with the "Desktop development with C++" workload (MSVC x64 + Windows SDK).
- CMake >= 3.25 on `PATH`, Git, Python 3 + pip.

No admin rights are needed for anything below.

## 2. Third-party sources (`external/`)

This fork's initial import does not contain the upstream submodule gitlinks, so `git submodule update` does nothing.
Fetch the exact commits upstream `master` pins (the import matches upstream `master`, version `24.0.0-beta.0`):

```powershell
cd <repo>\external
$subs = @(
  @('core_mqtt',  'https://github.com/freertos/coreMQTT',        '04845c6a8e5f9cf2d232f1c6e80baeb81302e690'),
  @('linalg',     'https://github.com/sgorsten/linalg.git',      '4460f1f5b85ccc81ffcf49aa450d454db58ca90e'),
  @('lunasvg',    'https://github.com/sammycage/lunasvg.git',    '83c58df8103dc7dca423dfd824992af94d49bed6'),
  @('mdns',       'https://github.com/mjansson/mdns.git',        '1727be0602941a714cb6048a737f0584b1cebf3c'),
  @('stb',        'https://github.com/nothings/stb.git',         'f7f20f39fe4f206c6f19e26ebfef7b261ee59ee4'),
  @('zstd',       'https://github.com/facebook/zstd.git',        '794ea1b0afca0f020f4e57b6732332231fb23c70')
)
foreach ($s in $subs) {
  $name, $url, $sha = $s
  if (Test-Path $name) { Remove-Item -Recurse -Force $name }
  git init -q $name
  git -C $name remote add origin $url
  git -C $name fetch -q --depth 1 origin $sha
  git -C $name checkout -q FETCH_HEAD
}
```

(`external/sqlite` is vendored and already present.)

## 3. Qt 6.8.3 + OpenSSL 3 (via aqtinstall)

```powershell
python -m pip install aqtinstall
python -m aqt install-qt windows desktop 6.8.3 win64_msvc2022_64 -m qtserialport -O C:\Code\deps\Qt
python -m aqt install-tool windows desktop tools_opensslv3_x64 qt.tools.opensslv3.win_x64 -O C:\Code\deps\Qt
```

Result: Qt in `C:\Code\deps\Qt\6.8.3\msvc2022_64`, OpenSSL 3.0.x (headers, import libs, DLLs) in `C:\Code\deps\Qt\Tools\OpenSSLv3\Win_x64`.

## 4. Configure and build Overglow

```powershell
cd <repo>
$env:CMAKE_PREFIX_PATH = 'C:\Code\deps\Qt\6.8.3\msvc2022_64\lib\cmake'
$env:OPENSSL_ROOT_DIR  = 'C:\Code\deps\Qt\Tools\OpenSSLv3\Win_x64'

cmake -S . -B build -G "Visual Studio 18 2026" -A x64 -DCMAKE_BUILD_TYPE=Release -DUSE_CCACHE_CACHING=OFF
cmake --build build --config Release -- '/nologo' '/v:m' '/m'
```

- With VS 2022 use `-G "Visual Studio 17 2022"` instead.
- Alternatively use the CMake presets, which encode the same settings and the `C:\Code\deps` paths above
  (put a `CMakeUserPresets.json` that inherits `win-base` next to it to use other paths):
  `cmake --preset win-release` then `cmake --build --preset win-release` (`win-debug` for a Debug build; both share `build`).
- Output: `build\Bin\Release\overglow.exe` plus the Qt/zstd DLLs (windeployqt runs as a post-build step).
- A clean build takes about 5 minutes. Expected warnings: `VCINSTALLDIR is not set` from windeployqt (harmless;
  set `$env:VCINSTALLDIR = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\'` to also deploy the VC runtime),
  C4756 in `ColorSpace.cpp`, and C4701/C4996 in third-party code.
- Optional installer: install NSIS and run `cmake --build build --config Release --target package`.
- `-DOVERGLOW_SUPPORT_URL=<url>` sets the support/documentation link shown by both web UIs and the installer (default: none).
- Icons, installer banners and the old web UI logos are generated from `branding/` by `python -I branding\generate_icons.py`.
- The product is Windows-only: the DirectX system grabber, sound capture, HID, mDNS, MQTT, power management, systray and zstd
  are always built. Build options (`-D<option>=ON|OFF`):
  - `USE_SHARED_LIBS` (ON): build sqlite, utils-image and utils-zstd as DLLs.
  - `USE_EMBEDDED_WEB_RESOURCES` (OFF): embed the web UI into the exe instead of `lib\web_resources.rcc`.
  - `USE_PRECOMPILED_HEADERS` (ON), `USE_CCACHE_CACHING` (ON, used only if ccache is found), `BUILD_TESTING` (ON).
  - OpenSSL is optional: without it the HTTPS server and Philips Hue (DTLS) are disabled.

## 5. Tests

Unit tests use Qt Test and run with ctest (`BUILD_TESTING` is ON by default). Each test is a separate executable in
`build\tests\<config>`, registered with `overglow_add_test()` from `cmake/cmake_modules/OverglowTest.cmake`; ctest puts the Qt
`bin` directory on `PATH` and runs Qt with the offscreen platform, so nothing needs to be deployed. Fixtures live in `tests/fixtures`.

```powershell
cd <repo>
cmake --build --preset win-release          # or only the tests: --target test_ledstring
ctest --preset win-release                  # all tests; -L unit for one label, -R <regex> for some tests, -V for the Qt Test log
```

## 6. Run

```powershell
$env:PATH = 'C:\Code\deps\Qt\Tools\OpenSSLv3\Win_x64\bin;' + $env:PATH   # OpenSSL DLLs are not copied into Bin
<repo>\build\Bin\Release\overglow.exe -d
```

Useful options: `--userdata <dir>` (own settings DB in `<dir>\db\overglow.db`), `--service` (console only, no tray icon), `-d`/`-v` (log level),
`--resetPassword`. The default web/API password is `overglow`. Without `--userdata` the data lives in `%LOCALAPPDATA%\Overglow`
(or `%USERPROFILE%\.overglow` when that folder exists).

### Migration of legacy data

On startup `sources/app/LegacyMigration.cpp` migrates data of the product this one is based on, never touching the legacy files:

- If the default data directory does not exist yet but the legacy default directory does, the legacy directory is copied
  and its database renamed to `db\overglow.db`.
- If a data directory (also a `--userdata` one) has only the legacy database file in `db\`, it is copied to `db\overglow.db`.
- A legacy database schema is upgraded in place (instance column name, default user; the legacy default password is replaced by `overglow`).

For tests, `OVERGLOW_DATA_DIR` and `OVERGLOW_LEGACY_DATA_DIR` override the new and the legacy default directories, so the
migration can be exercised without `--userdata` and without touching the real `%LOCALAPPDATA%` folders.

### Running next to an installed copy (dev side-by-side)

`main.cpp` refuses to start if another `overglow.exe` process is running, and a fresh DB uses the default web ports (8090/8092).
To run a dev build next to a production install without touching it:

1. Copy the exe to a different name (only the process name is checked):
   `Copy-Item build\Bin\Release\overglow.exe build\Bin\Release\overglow-test.exe`
2. Pre-seed a separate user-data DB with other ports and no grabbers (missing fields are filled from the schema defaults at first start).
   Save this as `seed-testdb.py` (anywhere) and run `python seed-testdb.py C:\Code\deps\testdata`:

```python
import json, os, sqlite3, sys
db_dir = os.path.join(sys.argv[1], "db")
os.makedirs(db_dir, exist_ok=True)
db = sqlite3.connect(os.path.join(db_dir, "overglow.db"))
db.execute("CREATE TABLE IF NOT EXISTS settings (type TEXT, config TEXT, instance INTEGER, updated_at TEXT)")
rows = [("webConfig", {"port": 18090, "sslPort": 18092}, None),
        ("systemControl", {"systemInstanceEnable": False}, 0),
        ("device", {"type": "file"}, 0)]
for t, c, i in rows:  # global settings have instance NULL, per-instance ones use the instance index
    db.execute("INSERT INTO settings VALUES (?, ?, ?, datetime('now'))", (t, json.dumps(c), i))
db.commit()
```

3. Start it: `build\Bin\Release\overglow-test.exe --service --userdata C:\Code\deps\testdata`
   (LED device `file` writes to `\\.\NUL`; the system grabber stays off).
4. Stop it by PID only (never by image name): `Get-Process overglow-test | Stop-Process`.

JSON-RPC: `config getconfig`/`setconfig` need admin rights. Local connections must log in first (`localAdminAuth` is on by default), which only
works on a persistent connection (WebSocket on the web port, e.g. `ws://127.0.0.1:18090/`):
`{"command":"authorize","subcommand":"login","password":"overglow"}`. Plain HTTP `POST /json-rpc` only accepts an
`Authorization: token <token>` header, which does not grant admin rights, so it returns `No Authorization` for getconfig/setconfig.
