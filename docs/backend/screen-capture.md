# Screen capture (Windows)

How Backbloom reads the desktop on Windows: monitor selection, DXGI desktop duplication, the GPU and CPU scaling paths,
HDR tone mapping and recovery from capture errors.

This is a port of HyperHDR's DX grabber (`DxGrabber`, `Grabber`, `SystemWrapper`, `LutLoader`, `DefaultLut`).
`DxgiScreenSource` (in `Backbloom.Capture.Windows`) runs entirely on the frame loop thread, because D3D11's immediate
context is single-threaded. It initializes lazily, retries after failures and rebuilds the duplication when it is lost.

## Contents

- [Capture stages](#capture-stages)
- [Interop](#interop)
- [Shaders](#shaders)
- [Measurements](#measurements)

## Capture stages

| Stage | Behavior |
|---|---|
| Monitors | `MonitorEnumerator`: DXGI outputs plus the display configuration API. Setting values: `auto` (primary monitor; HyperHDR took the first sorted entry, which could be a multi-monitor one), `\\.\DISPLAY5\|NVIDIA GeForce RTX 5080` (HyperHDR's id, kept so imported settings work and the UI can show it), the stable monitor device path (or `edid:manufacturer:product:connector` when the driver reports none), or `MULTI-MONITOR\|adapter` (all monitors of an adapter side by side, ordered by `reorder_displays` as `std::next_permutation` did). Windows renumbers the GDI names after a driver update or a reconnect (`DISPLAY5` came back as `DISPLAY9`), so a single-monitor setting (`MonitorEnumerator.SelectFor`, a fallback logged once as a warning) captures the monitor with the device path it matched last in this run (also when another monitor took the old name); one that matched nothing yet in this run captures the only connected monitor, if there is just one (not remembered, so a monitor that is away, like a TV switched off, is waited for and not replaced by the remaining screen). Still open (see the [roadmap](../roadmap.md#product-and-ux)): after a restart with several monitors a stale id matches nothing (or, if another monitor took the old name, that one), and the UI only offers Auto and the captured monitor. |
| Duplication | `IDXGIOutput6::DuplicateOutput1` (the capture thread is made per-monitor DPI aware, otherwise it fails with `DXGI_ERROR_UNSUPPORTED`). HDR is detected per monitor (`DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020`); with hardware on and an SDR white level above 0 (see SDR white below) the desktop is duplicated as FP16 scRGB (10-bit PQ as a fallback), else as 8-bit BGRA, which Windows converts to SDR. |
| GPU path (`hardware`) | `GpuScaler`: the acquired texture is copied into an own texture (the frame is released at once), `Shaders/Downscale.hlsl` averages each `divide` x `divide` block of the cropped area (bilinear taps between texels, up to 8 x 8 per pixel) into a small BGRA target, then one staging readback of that size. Modes: scRGB scaled by 80 / SDR white level + BT.709 OETF (HyperHDR's HDR shader, reconstructed from its bytecode), PQ/BT.2020 -> BT.709, the LUT as a 256^3 RGBA8 3D texture read with exact texel loads (8-bit captures that tone mapping applies to, see below), or plain SDR. The shader only runs when the image or the mode changed. |
| CPU path | `CpuScaler` + `BgraDownscaler` (port of `decodeBGRA`): full-size 8-bit readback, every `divide`-th pixel, LUT on the CPU. Used when `hardware` is off or the shaders can't be created. |
| Scaling, crop | `CaptureRegion`: the smallest whole factor that makes the cropped width fit `videoMode` (as `getTargetSystemFrameDimension`). Crops are in screen pixels on both paths (HyperHDR's GPU path cropped the scaled image) and are ignored when they leave 16 pixels or less. |
| Pacing, latency | `fps` via `FramePacer`; then `AcquireNextFrame(0)` returns the newest desktop image (no new image: the last one is delivered again from the staging texture; only the pointer moved: no GPU work). Until the first image after (re)start it waits up to 100 ms. |
| Recovery | `DXGI_ERROR_ACCESS_LOST` / invalid call / device removed (display mode or HDR change, desktop switch, driver reset): everything is released and rebuilt 100 ms later. `E_ACCESSDENIED` (secure desktop: UAC, lock screen): retried every second; the thread attaches to the input desktop (`OpenInputDesktop` + `SetThreadDesktop`) so a service can follow it. Missing monitor: retried every 5 s. A retry waits instead of spinning; the same problem is logged once. |
| Tone mapping | `hdrToneMapping` (`ToneMapping.AppliesLut`): `auto` (default) applies the LUT to the 8-bit capture of a monitor only while Windows drives it in HDR mode, `on` to every 8-bit capture (HDR10 content shown without Windows HDR), `off` never. An HDR capture (FP16/PQ) is always converted by the shader, never by the LUT. The decision is made when a monitor's duplication opens; toggling Windows HDR ends the duplication, so it follows within ~100 ms. HyperHDR's switch applied the LUT to SDR monitors too, which oversaturates normal content (red/magenta shift); imports and older databases turn on into `auto` and off into `off`. `serverinfo.systemGrabbers.hdr` = `{ mode, capturing, monitorHdr, toneMapping, sdrWhiteNits }` (tone mapped by the shader or the LUT; the SDR white level of an HDR capture, 0 without one), pushed as `capture-update` (the whole `systemGrabbers` object) when it changes. |
| SDR white | The level an HDR desktop is converted with (`CaptureSettings.SdrWhiteNits`). With `monitor_nits_auto` (default, a Backbloom addition) it is Windows' "SDR content brightness" of the monitor: `MonitorEnumerator` reads `DisplayConfigGetDeviceInfo(DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL)` (`SDRWhiteLevel`, 1000 = 80 nits) with the monitor names when the capture starts, and the capture loop asks again every 2 s (one non-allocating call, since a brightness change alone needn't end the duplication) and reopens when it changed. Without a reported level, and with the switch off, `monitor_nits` applies (0 turns the HDR capture off). |
| LUT | `LutLoader`: first existing `lut_lin_tables.3d` or `.3d.zst` wins; one table or the legacy three-table file (first table used). `LutProvider` loads in the background on first use (only when tone mapping applies to an 8-bit capture) and otherwise builds the default table (`DefaultLut`, bit-identical to HyperHDR's, ~0.3 s on 24 cores) and saves it to the first LUT directory. |
| Settings | `systemGrabber` (schema in `Backbloom.Settings`); `App` reconfigures the source on every change of that section, and the next frame restarts the capture. |

## Interop

Capture uses [TerraFX.Interop.Windows](https://github.com/terrafx/terrafx.interop.windows) (MIT), a 1:1 projection of
the Windows SDK headers as blittable structs and function pointers. Reasons:

- the port reads like the C++ code;
- COM calls allocate nothing (no runtime-callable wrappers, unlike Vortice's class wrappers, which allocate per acquired
  frame);
- it is trimming/AOT compatible with no source generator configuration (CsWin32 would need one per API, and its COM
  wrappers marshal by default).

Unused parts of the large assembly are trimmed when published.

## Shaders

`Shaders/Downscale.hlsl` is the source; the bytecode (`Downscale.vs.cso`, `Downscale.ps.cso`, embedded) is checked in,
so building needs no Windows SDK. `Shaders.targets` recompiles it with the SDK's `fxc` (vs_5_0/ps_5_0, `/O3 /WX`) when
the HLSL is newer; fxc output is deterministic. The resources are marked `WithCulture="false"` (MSBuild would otherwise
treat `Downscale.ps.cso` as a Pashto satellite resource).

## Measurements

On an RTX 5080 with a 3440x1440 desktop texture scaled to 491x205 (Release, 300 frames): the GPU path takes 0.21 ms per
frame for FP16 HDR and 0.13 ms for 8-bit (copy, shader, readback; 0.31 / 0.21 ms CPU time); the CPU path takes 0.78 ms.
Neither path allocates. Waiting for a monitor that delivers no frames costs 0.1-0.3 % of one core.
