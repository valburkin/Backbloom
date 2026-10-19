# Building HyperHDR on Windows (from source)

Verified recipe on Windows 11 x64 with Visual Studio 2026 (18.x) Community, CMake 4.3, Python 3.12.
All third-party SDKs go to `C:\Code\deps` (outside the repository); the build goes to `C:\Code\HyperHDR\build` (git-ignored).
All commands are PowerShell, run from any directory unless stated otherwise.

## 1. Prerequisites

- Visual Studio 2026 (or 2022) with the "Desktop development with C++" workload (MSVC x64 + Windows SDK).
- CMake >= 3.25 on `PATH`, Git, Python 3 + pip.

No admin rights are needed for anything below.

## 2. Third-party sources (`external/`)

This fork's initial import does not contain the upstream submodule gitlinks, so `git submodule update` does nothing.
Fetch the exact commits upstream `master` pins (the import matches upstream `master`, version `24.0.0-beta.0`):

```powershell
cd C:\Code\HyperHDR\external
$subs = @(
  @('core_mqtt',  'https://github.com/freertos/coreMQTT',        '04845c6a8e5f9cf2d232f1c6e80baeb81302e690'),
  @('flatbuffers','https://github.com/google/flatbuffers',       '595bf0007ab1929570c7671f091313c8fc20644e'),
  @('linalg',     'https://github.com/sgorsten/linalg.git',      '4460f1f5b85ccc81ffcf49aa450d454db58ca90e'),
  @('lunasvg',    'https://github.com/sammycage/lunasvg.git',    '83c58df8103dc7dca423dfd824992af94d49bed6'),
  @('mdns',       'https://github.com/mjansson/mdns.git',        '1727be0602941a714cb6048a737f0584b1cebf3c'),
  @('nanopb',     'https://github.com/nanopb/nanopb.git',        '6cfe48d6f1593f8fa5c0f90437f5e6522587745e'),
  @('rpi_ws281x', 'https://github.com/jgarff/rpi_ws281x',        '22f2e35012704845d82e06d2fa2e10fbc810ecaa'),
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

## 4. libjpeg-turbo 3.0.3 (built from source)

The official `libjpeg-turbo-3.0.3-vc64.exe` installer requires administrator rights, so build it instead (NASM enables the SIMD code):

```powershell
mkdir C:\Code\deps\downloads, C:\Code\deps\src -Force | Out-Null
curl.exe -sL -o C:\Code\deps\downloads\nasm.zip https://www.nasm.us/pub/nasm/releasebuilds/2.16.03/win64/nasm-2.16.03-win64.zip
curl.exe -sL -o C:\Code\deps\downloads\ljt.tar.gz https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.0.3/libjpeg-turbo-3.0.3.tar.gz
Expand-Archive C:\Code\deps\downloads\nasm.zip -DestinationPath C:\Code\deps -Force
tar -xzf C:\Code\deps\downloads\ljt.tar.gz -C C:\Code\deps\src

cmake -S C:\Code\deps\src\libjpeg-turbo-3.0.3 -B C:\Code\deps\src\libjpeg-turbo-build `
  -G "Visual Studio 18 2026" -A x64 `
  -DCMAKE_INSTALL_PREFIX=C:/Code/deps/libjpeg-turbo64 `
  -DCMAKE_ASM_NASM_COMPILER=C:/Code/deps/nasm-2.16.03/nasm.exe `
  -DENABLE_STATIC=OFF -DWITH_TURBOJPEG=ON
cmake --build C:\Code\deps\src\libjpeg-turbo-build --config Release --target install -- -m
```

`C:\Code\deps\libjpeg-turbo64\{include,lib,bin}` is the layout `cmake/cmake_modules/FindTurboJPEG.cmake` expects under `$env:TOOLKIT_DIR`.

## 5. Configure and build HyperHDR

```powershell
cd C:\Code\HyperHDR
$env:TOOLKIT_DIR       = 'C:\Code\deps'                                   # finds libjpeg-turbo64
$env:CMAKE_PREFIX_PATH = 'C:\Code\deps\Qt\6.8.3\msvc2022_64\lib\cmake'
$env:OPENSSL_ROOT_DIR  = 'C:\Code\deps\Qt\Tools\OpenSSLv3\Win_x64'

cmake -S . -B build -G "Visual Studio 18 2026" -A x64 -DPLATFORM=windows -DCMAKE_BUILD_TYPE=Release -DUSE_CCACHE_CACHING=OFF
cmake --build build --config Release -- -nologo -v:m -maxcpucount
```

- With VS 2022 use `-G "Visual Studio 17 2022"` instead.
- Output: `build\Bin\Release\hyperhdr.exe` plus the Qt/turbojpeg/zstd DLLs (windeployqt runs as a post-build step).
- A clean build takes about 5 minutes. Expected warnings: `VCINSTALLDIR is not set` from windeployqt (harmless;
  set `$env:VCINSTALLDIR = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\'` to also deploy the VC runtime),
  C4756 in `ColorSpace.cpp`, and C4701/C4996 in third-party code.
- Optional installer: install NSIS and run `cmake --build build --config Release --target package`.
- The build is configured with every Windows feature on (DX, Media Foundation, sound capture, HID, SPI-FTDI, Bonjour, MQTT, protobuf, systray, zstd, OpenSSL).

## 6. Run

```powershell
$env:PATH = 'C:\Code\deps\Qt\Tools\OpenSSLv3\Win_x64\bin;' + $env:PATH   # OpenSSL DLLs are not copied into Bin
C:\Code\HyperHDR\build\Bin\Release\hyperhdr.exe -d
```

Useful options: `--userdata <dir>` (own settings DB in `<dir>\db\hyperhdr.db`), `--service` (console only, no tray icon), `-d`/`-v` (log level),
`--resetPassword`. The default web/API password is `hyperhdr`.

### Running next to an installed HyperHDR (dev side-by-side)

`main.cpp` refuses to start if another `hyperhdr.exe` process is running, and a fresh DB uses the default ports (8090/8092/19444/19400).
To run a dev build next to a production install without touching it:

1. Copy the exe to a different name (only the process name is checked):
   `Copy-Item build\Bin\Release\hyperhdr.exe build\Bin\Release\hyperhdr-test.exe`
2. Pre-seed a separate user-data DB with other ports and no grabbers (missing fields are filled from the schema defaults at first start).
   Save this as `seed-testdb.py` (anywhere) and run `python seed-testdb.py C:\Code\deps\testdata`:

```python
import json, os, sqlite3, sys
db_dir = os.path.join(sys.argv[1], "db")
os.makedirs(db_dir, exist_ok=True)
db = sqlite3.connect(os.path.join(db_dir, "hyperhdr.db"))
db.execute("CREATE TABLE IF NOT EXISTS settings (type TEXT, config TEXT, hyperhdr_instance INTEGER, updated_at TEXT)")
rows = [("webConfig", {"port": 18090, "sslPort": 18092}, None), ("jsonServer", {"port": 29444}, None),
        ("flatbufServer", {"enable": True, "port": 29400}, None), ("protoServer", {"enable": False, "port": 29445}, None),
        ("videoControl", {"videoInstanceEnable": False}, 0), ("systemControl", {"systemInstanceEnable": False}, 0),
        ("device", {"type": "file"}, 0)]
for t, c, i in rows:  # global settings have hyperhdr_instance NULL, per-instance ones use the instance index
    db.execute("INSERT INTO settings VALUES (?, ?, ?, datetime('now'))", (t, json.dumps(c), i))
db.commit()
```

3. Start it: `build\Bin\Release\hyperhdr-test.exe --service --userdata C:\Code\deps\testdata`
   (LED device `file` writes to `\\.\NUL`; video/system grabbers stay off).
4. Stop it by PID only (never by image name): `Get-Process hyperhdr-test | Stop-Process`.

JSON-RPC: `config getconfig`/`setconfig` need admin rights. Local connections must log in first (`localAdminAuth` is on by default), which only
works on a persistent connection (TCP JSON server, port 29444 above, newline-delimited JSON, or WebSocket):
`{"command":"authorize","subcommand":"login","password":"hyperhdr"}`. Plain HTTP `POST /json-rpc` only accepts an
`Authorization: token <token>` header, which does not grant admin rights, so it returns `No Authorization` for getconfig/setconfig.
