# Building Overglow

Windows is the primary platform and has its own step-by-step guide: [BUILDING-WINDOWS.md](BUILDING-WINDOWS.md).
This page covers native Linux and macOS builds, which compile from the same sources but are not currently tested.

## Build dependencies

### Debian/Ubuntu

```console
sudo apt-get update

sudo apt-get install build-essential cmake git libasound2-dev libdbus-1-dev libegl-dev libgl-dev libglvnd-dev liblzma-dev libpipewire-0.3-dev libssl-dev libturbojpeg0-dev libzstd-dev ninja-build patchelf pkg-config python3 qt6-serialport-dev qt6-base-dev unzip wget chrpath
```

### Fedora

```console
sudo dnf -y install alsa-lib-devel chrpath cmake dbus-devel fedora-packager gcc gcc-c++ git libglvnd-devel libzstd-devel mesa-libEGL-devel mesa-libGL-devel ninja-build openssl-devel pipewire-devel pkg-config qt6-qtbase-devel qt6-qtserialport-devel turbojpeg-devel unzip wget xz-devel chrpath

```

### Arch Linux

```console
sudo pacman -Syy

sudo pacman -S alsa-lib base-devel bash binutils chrpath cmake dbus dpkg fakeroot freetds git libfbclient libglvnd libjpeg-turbo mariadb-libs mesa ninja openssl pipewire pkgfile postgresql-libs python qt6-base qt6-serialport unzip wayland wget xz chrpath
```

### macOS
First install [brew](https://brew.sh/) manager.  
Next: `brew install qtbase qtserialport cmake xz pkg-config`

## Compiling

The third-party sources in `external/` must be present (see section 2 of [BUILDING-WINDOWS.md](BUILDING-WINDOWS.md)
for the pinned commits).

```bash
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j $(nproc)

# run it from the build directory
build/bin/overglow -d

# optional: installer packages (DEB/RPM/TGZ on Linux, DMG on macOS)
cmake --build build --target package
```

Set `-DOVERGLOW_SUPPORT_URL=<url>` to show a support/documentation link in the web UIs and installers.

## CMake configuration options
Use -D prefix when configuring the build.

* LED DEVICES  
    * ENABLE_HID = ON | OFF, enables HID LED devices (Robobloq SyncLight)
  
* SOFTWARE GRABBERS
    * ENABLE_DX = ON | OFF, enables the DirectX11 software grabber (Windows)
    * ENABLE_MAC_SYSTEM = ON | OFF, enables the macOS software grabber (macOS)
    * ENABLE_PIPEWIRE = ON | OFF, enables the Pipewire software grabber (Linux)
    * ENABLE_PIPEWIRE_EGL = ON | OFF, enables EGL for the Pipewire grabber (Linux)
    * ENABLE_X11 = ON | OFF, enables the X11 software grabber (Linux)

* HARDWARE GRABBERS
    * ENABLE_AVF = ON | OFF, enables the AVF USB grabber support (macOS)
    * ENABLE_MF = ON | OFF, enables the MediaFoundation USB grabber support (Windows)
    * ENABLE_V4L2 = ON | OFF, enables the V4L2 USB grabber support (Linux)

* SOUND CAPTURING
    * ENABLE_SOUNDCAPLINUX = ON | OFF, enables the ALSA sound grabber (Linux)
    * ENABLE_SOUNDCAPMACOS = ON | OFF, enables the sound grabber (macOS)
    * ENABLE_SOUNDCAPWINDOWS = ON | OFF, enables the sound grabber (Windows)

* SERVICE SUPPORT
    * ENABLE_BONJOUR = ON | OFF, enables mDNS (do not disable unless required)
    * ENABLE_MQTT = ON | OFF, enables the MQTT support
    * ENABLE_POWER_MANAGEMENT = ON | OFF, enables sleep/wake up OS events support
    * ENABLE_SYSTRAY = ON | OFF, enables the systray-widget
    * ENABLE_ZSTD = ON | OFF, enables ZSTD support for LUT decompression

* BUILD FEATURES
    * USE_SHARED_LIBS = ON | OFF, build the application as non-monolithic
    * USE_EMBEDDED_WEB_RESOURCES = ON | OFF, embed web content into the app
    * USE_PRECOMPILED_HEADERS = ON | OFF, use pre-compiled headers when building
    * USE_CCACHE_CACHING = ON | OFF, enable CCache support if available
    * USE_SYSTEM_LUNASVG_LIBS = ON | OFF, prefer system lunasvg libs
    * USE_SYSTEM_STB_LIBS = ON | OFF, prefer system stb libs
    * USE_STATIC_QT_PLUGINS = ON | OFF, embed static QT-plugins into the app
    * USE_STANDARD_INSTALLER_NAME = ON | OFF, use standard Linux package naming
    * ENABLE_DEPENDENCY_PACKAGING = ON | OFF, bundle dependency libs into the Linux installer
