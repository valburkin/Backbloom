using System.Runtime.Versioning;
using Backbloom.Capture.Hdr;
using Backbloom.Core.Imaging;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DXGI;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.DirectX.DXGI_MODE_ROTATION;
using static TerraFX.Interop.Windows.Windows;

namespace Backbloom.Capture.Windows;

/// <summary>Outcome of capturing one monitor.</summary>
internal enum DisplayResult
{
    /// <summary>A new desktop image was captured.</summary>
    NewImage,

    /// <summary>The desktop hasn't changed; the previous image was delivered again.</summary>
    SameImage,

    /// <summary>No image yet (nothing captured since the duplication started).</summary>
    NoImage,

    /// <summary>The duplication is gone (mode change, desktop switch, device lost): reinitialize.</summary>
    Lost,
}

/// <summary>
/// Desktop duplication of one monitor (port of the per-display part of DxGrabber: DisplayHandle, initDirectX,
/// captureFrame). The acquired desktop image is handed to a <see cref="GpuScaler"/> (hardware acceleration) or a
/// <see cref="CpuScaler"/> and the duplication frame is released right away.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed unsafe class DisplayCapture : IDisposable
{
    private const uint FirstImageTimeoutMs = 100;

    private readonly IScaler _scaler;
    private IDXGIOutputDuplication* _duplication;
    private bool _hasImage;

    private DisplayCapture(IDXGIOutputDuplication* duplication, OutputLocation location, bool wideGamut, int sdrWhiteNits, bool appliesLut, DXGI_OUTDUPL_DESC desc, IScaler scaler, CaptureRegion region)
    {
        _duplication = duplication;
        Monitor = location.Info;
        Target = location.Target;
        WindowsSdrWhiteNits = location.SdrWhiteNits;
        WideGamut = wideGamut;
        SdrWhiteNits = wideGamut ? sdrWhiteNits : 0;
        AppliesLut = appliesLut;
        Format = desc.ModeDesc.Format;
        Rotation = desc.Rotation;
        _scaler = scaler;
        Region = region;
    }

    public MonitorInfo Monitor { get; }

    /// <summary>The monitor's display configuration target, to watch its SDR white level (null when unknown).</summary>
    public DisplayTarget? Target { get; }

    /// <summary>Windows' SDR white level of the monitor when the capture was opened (0 when not reported).</summary>
    public int WindowsSdrWhiteNits { get; }

    /// <summary>True when the desktop is captured as HDR (FP16 scRGB or 10-bit PQ) and converted by the shader.</summary>
    public bool WideGamut { get; }

    /// <summary>The SDR white level the shader converts the HDR desktop with (0 without <see cref="WideGamut"/>).</summary>
    public int SdrWhiteNits { get; }

    /// <summary>True when the tone mapping table is applied to this monitor's 8-bit image.</summary>
    public bool AppliesLut { get; }

    /// <summary>Format of the duplicated desktop.</summary>
    public DXGI_FORMAT Format { get; }

    public DXGI_MODE_ROTATION Rotation { get; }

    /// <summary>True when the duplicated image is rotated: it is captured in scanout orientation.</summary>
    public bool IsRotated => Rotation is DXGI_MODE_ROTATION_ROTATE90 or DXGI_MODE_ROTATION_ROTATE180 or DXGI_MODE_ROTATION_ROTATE270;

    public bool UsesGpu => _scaler is GpuScaler;

    /// <summary>Crop and scale of this monitor.</summary>
    public CaptureRegion Region { get; }

    /// <summary>
    /// Starts duplicating <paramref name="output"/>. HDR (wide gamut) capture is used when Windows HDR is on for the
    /// monitor, its SDR white level (<see cref="CaptureSettings.SdrWhiteNits"/>: Windows' or <c>monitor_nits</c>)
    /// isn't 0 and the shaders are available; otherwise the desktop is captured as 8-bit BGRA, which Windows converts to
    /// SDR. Whether the tone mapping table applies is decided here too, so a change of the monitor's HDR mode (which
    /// ends the duplication) takes effect when it is reopened.
    /// </summary>
    public static DisplayCapture Open(GpuDevice gpu, IDXGIOutput6* output, OutputLocation location, CaptureSettings settings)
    {
        var monitor = location.Info;
        var useGpu = settings.Hardware && gpu.HasShaders;
        var device = (IUnknown*)gpu.Device;
        HRESULT hr = E.E_FAIL;
        IDXGIOutputDuplication* duplication = null;
        var wideGamut = false;
        var sdrWhiteNits = settings.SdrWhiteNits(location.SdrWhiteNits);
        if (useGpu && monitor.Hdr && sdrWhiteNits > 0)
        {
            var wide = stackalloc DXGI_FORMAT[] { DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R10G10B10A2_UNORM };
            hr = output->DuplicateOutput1(device, 0, 2, wide, &duplication);
            wideGamut = hr.SUCCEEDED;
        }

        if (!wideGamut)
        {
            var bgra = DXGI_FORMAT_B8G8R8A8_UNORM;
            hr = output->DuplicateOutput1(device, 0, 1, &bgra, &duplication);
        }

        Com.Check(hr, "IDXGIOutput6::DuplicateOutput1");
        IScaler? scaler = null;
        try
        {
            DXGI_OUTDUPL_DESC desc;
            duplication->GetDesc(&desc);
            var width = (int)desc.ModeDesc.Width;
            var height = (int)desc.ModeDesc.Height;
            var region = CaptureRegion.Compute(width, height, settings.Crop, settings.MaxWidth);
            var appliesLut = ToneMapping.AppliesLut(settings.ToneMapping, monitor.Hdr, wideGamut);
            scaler = useGpu
                ? new GpuScaler(gpu, width, height, region, desc.ModeDesc.Format, wideGamut, sdrWhiteNits, appliesLut)
                : new CpuScaler(gpu, width, height, region, appliesLut);
            return new DisplayCapture(duplication, location, wideGamut, sdrWhiteNits, appliesLut, desc, scaler, region);
        }
        catch
        {
            scaler?.Dispose();
            Com.Release(ref duplication);
            throw;
        }
    }

    /// <summary>
    /// Captures the monitor into <paramref name="frame"/> at column <paramref name="x"/>. When the desktop hasn't
    /// changed, the last image is delivered again. <paramref name="presentTime"/> receives the QPC time the new image
    /// was presented (0 when there is none).
    /// </summary>
    public DisplayResult Capture(Frame frame, int x, ToneMappingLut? lut, out long presentTime)
    {
        presentTime = 0;
        DXGI_OUTDUPL_FRAME_INFO info;
        IDXGIResource* resource = null;
        // Until the first image arrives (it can take a few refreshes after the duplication starts), wait for it.
        HRESULT hr = _duplication->AcquireNextFrame(_hasImage ? 0u : FirstImageTimeoutMs, &info, &resource);
        var result = DisplayResult.SameImage;
        if (hr == DXGI_ERROR_WAIT_TIMEOUT)
        {
            if (!_hasImage)
            {
                return DisplayResult.NoImage;
            }
        }
        else if (hr.FAILED)
        {
            return DisplayResult.Lost;
        }
        else
        {
            try
            {
                // LastPresentTime is 0 when only the mouse pointer moved: the desktop image is unchanged.
                if (info.LastPresentTime.QuadPart != 0 || !_hasImage)
                {
                    ID3D11Texture2D* texture = null;
                    if (resource->QueryInterface(__uuidof<ID3D11Texture2D>(), (void**)&texture).FAILED)
                    {
                        return DisplayResult.Lost;
                    }

                    try
                    {
                        _scaler.CopyFrom(texture);
                    }
                    finally
                    {
                        Com.Release(ref texture);
                    }

                    presentTime = info.LastPresentTime.QuadPart;
                    _hasImage = true;
                    result = DisplayResult.NewImage;
                }
            }
            finally
            {
                Com.Release(ref resource);
                _duplication->ReleaseFrame();
            }
        }

        return _scaler.WriteTo(frame, x, lut) ? result : DisplayResult.Lost;
    }

    public void Dispose()
    {
        _scaler.Dispose();
        Com.Release(ref _duplication);
    }
}
