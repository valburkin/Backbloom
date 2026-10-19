using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Overglow.Capture.Hdr;
using Overglow.Core.Imaging;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D_PRIMITIVE_TOPOLOGY;
using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.D3D11_CPU_ACCESS_FLAG;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace Overglow.Capture.Windows;

/// <summary>Values of the <c>Params</c> constant buffer of Downscale.hlsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShaderParams
{
    public const uint ModeSdr = 0;
    public const uint ModeScRgb = 1;
    public const uint ModePq = 2;
    public const uint ModeLut = 3;

    public float OriginX;
    public float OriginY;
    public float InvSourceWidth;
    public float InvSourceHeight;
    public float Block;
    public uint Taps;
    public uint Mode;
    public float Scale;
}

/// <summary>Turns a captured desktop texture into a scaled-down BGRA image in a <see cref="Frame"/>.</summary>
[SupportedOSPlatform("windows10.0.17763")]
internal unsafe interface IScaler : IDisposable
{
    /// <summary>Takes a copy of the desktop image (the duplication frame is released right after).</summary>
    void CopyFrom(ID3D11Texture2D* desktop);

    /// <summary>Converts the last copied image and writes it into the frame at column <paramref name="x"/>; false when the device is gone.</summary>
    bool WriteTo(Frame frame, int x, ToneMappingLut? lut);
}

/// <summary>
/// GPU path: the desktop is copied into an own texture, scaled down and tone mapped by Downscale.hlsl into a small
/// BGRA render target, and read back through a staging texture of that size (port of DxGrabber::deepScaledCopy). The
/// shader only runs when the image or the conversion changed.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed unsafe class GpuScaler : IScaler
{
    /// <summary>Upper bound of samples per output pixel and axis.</summary>
    private const int MaxTaps = 8;

    private readonly GpuDevice _gpu;
    private readonly CaptureRegion _region;
    private readonly DXGI_FORMAT _format;
    private readonly bool _wideGamut;
    private readonly int _monitorNits;
    private readonly bool _toneMapping;
    private ID3D11Texture2D* _desktop;
    private ID3D11ShaderResourceView* _desktopView;
    private ID3D11Texture2D* _target;
    private ID3D11RenderTargetView* _targetView;
    private ID3D11Texture2D* _staging;
    private ID3D11Buffer* _params;
    private ShaderParams _shaderParams;
    private bool _paramsDirty = true;
    private bool _imageDirty;

    /// <param name="gpu">Device with shaders.</param>
    /// <param name="sourceWidth">Width of the desktop texture.</param>
    /// <param name="sourceHeight">Height of the desktop texture.</param>
    /// <param name="region">Crop and scale.</param>
    /// <param name="format">Format of the desktop texture.</param>
    /// <param name="wideGamut">True for an HDR desktop (FP16 scRGB or 10-bit PQ), converted with <paramref name="monitorNits"/> as SDR white.</param>
    /// <param name="monitorNits">SDR white level for <paramref name="wideGamut"/> images.</param>
    /// <param name="toneMapping">Apply the LUT to 8-bit images.</param>
    public GpuScaler(GpuDevice gpu, int sourceWidth, int sourceHeight, CaptureRegion region, DXGI_FORMAT format, bool wideGamut, int monitorNits, bool toneMapping)
    {
        _gpu = gpu;
        _region = region;
        _format = format;
        _wideGamut = wideGamut;
        _monitorNits = Math.Max(1, monitorNits);
        _toneMapping = toneMapping;
        try
        {
            _target = gpu.CreateTexture((uint)region.TargetWidth, (uint)region.TargetHeight, DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_USAGE_DEFAULT, (uint)D3D11_BIND_RENDER_TARGET, 0);
            ID3D11RenderTargetView* targetView = null;
            Com.Check(gpu.Device->CreateRenderTargetView((ID3D11Resource*)_target, null, &targetView), "CreateRenderTargetView");
            _targetView = targetView;
            _staging = gpu.CreateTexture((uint)region.TargetWidth, (uint)region.TargetHeight, DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_USAGE_STAGING, 0, (uint)D3D11_CPU_ACCESS_READ);
            _desktop = gpu.CreateTexture((uint)sourceWidth, (uint)sourceHeight, format, D3D11_USAGE_DEFAULT, (uint)D3D11_BIND_SHADER_RESOURCE, 0);
            ID3D11ShaderResourceView* desktopView = null;
            Com.Check(gpu.Device->CreateShaderResourceView((ID3D11Resource*)_desktop, null, &desktopView), "CreateShaderResourceView");
            _desktopView = desktopView;

            var bufferDesc = new D3D11_BUFFER_DESC { ByteWidth = (uint)sizeof(ShaderParams), Usage = D3D11_USAGE_DEFAULT, BindFlags = (uint)D3D11_BIND_CONSTANT_BUFFER };
            ID3D11Buffer* buffer = null;
            Com.Check(gpu.Device->CreateBuffer(&bufferDesc, null, &buffer), "CreateBuffer");
            _params = buffer;
        }
        catch
        {
            Dispose();
            throw;
        }

        _shaderParams = new ShaderParams
        {
            OriginX = region.X,
            OriginY = region.Y,
            InvSourceWidth = 1f / sourceWidth,
            InvSourceHeight = 1f / sourceHeight,
            Block = region.Divide,
        };
    }

    /// <summary>Shader mode in use (one of the <see cref="ShaderParams"/> constants).</summary>
    public uint Mode => _shaderParams.Mode;

    public void CopyFrom(ID3D11Texture2D* desktop)
    {
        _gpu.CopyOverlap(_desktop, desktop);
        _imageDirty = true;
    }

    public bool WriteTo(Frame frame, int x, ToneMappingLut? lut)
    {
        UpdateParams(lut);
        if (_imageDirty || _paramsDirty)
        {
            Render(lut);
            _imageDirty = false;
        }

        if (!_gpu.TryMap(_staging, out var data, out var pitch))
        {
            return false;
        }

        try
        {
            var rowBytes = _region.TargetWidth * Frame.BytesPerPixel;
            var pixels = frame.Pixels;
            for (var y = 0; y < _region.TargetHeight; y++)
            {
                new ReadOnlySpan<byte>(data + ((nint)y * pitch), rowBytes).CopyTo(pixels.Slice((y * frame.Stride) + (x * Frame.BytesPerPixel), rowBytes));
            }
        }
        finally
        {
            _gpu.Unmap(_staging);
        }

        return true;
    }

    public void Dispose()
    {
        Com.Release(ref _params);
        Com.Release(ref _staging);
        Com.Release(ref _targetView);
        Com.Release(ref _target);
        Com.Release(ref _desktopView);
        Com.Release(ref _desktop);
    }

    /// <summary>Picks the shader mode; the LUT applies to 8-bit images when tone mapping is on and the table is loaded.</summary>
    private void UpdateParams(ToneMappingLut? lut)
    {
        uint mode;
        float scale = 1;
        if (_wideGamut)
        {
            mode = _format == DXGI_FORMAT_R10G10B10A2_UNORM ? ShaderParams.ModePq : ShaderParams.ModeScRgb;
            scale = (mode == ShaderParams.ModePq ? 10000f : 80f) / _monitorNits;
        }
        else
        {
            mode = _toneMapping && lut is not null ? ShaderParams.ModeLut : ShaderParams.ModeSdr;
        }

        // A bilinear sample between texels averages 2x2 of them; LUT lookups need exact texels.
        var taps = (uint)(mode == ShaderParams.ModeLut ? Math.Min(_region.Divide, MaxTaps) : Math.Clamp((_region.Divide + 1) / 2, 1, MaxTaps));
        if (mode != _shaderParams.Mode || taps != _shaderParams.Taps || scale != _shaderParams.Scale)
        {
            _shaderParams.Mode = mode;
            _shaderParams.Taps = taps;
            _shaderParams.Scale = scale;
            _paramsDirty = true;
        }
    }

    private void Render(ToneMappingLut? lut)
    {
        var context = _gpu.Context;
        if (_paramsDirty)
        {
            var p = _shaderParams;
            context->UpdateSubresource((ID3D11Resource*)_params, 0, null, &p, 0, 0);
            _paramsDirty = false;
        }

        var targetView = _targetView;
        context->OMSetRenderTargets(1, &targetView, null);
        var viewport = new D3D11_VIEWPORT { Width = _region.TargetWidth, Height = _region.TargetHeight, MaxDepth = 1 };
        context->RSSetViewports(1, &viewport);
        context->IASetInputLayout(null);
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(_gpu.VertexShader, null, 0);
        context->PSSetShader(_gpu.PixelShader, null, 0);
        var views = stackalloc ID3D11ShaderResourceView*[2];
        views[0] = _desktopView;
        views[1] = _shaderParams.Mode == ShaderParams.ModeLut && lut is not null ? _gpu.GetLutView(lut) : null;
        context->PSSetShaderResources(0, 2, views);
        var sampler = _gpu.Sampler;
        context->PSSetSamplers(0, 1, &sampler);
        var buffer = _params;
        context->PSSetConstantBuffers(0, 1, &buffer);
        context->Draw(3, 0);

        // Unbind the desktop copy so the next copy into it doesn't conflict with the shader input.
        views[0] = null;
        views[1] = null;
        context->PSSetShaderResources(0, 2, views);
        context->CopyResource((ID3D11Resource*)_staging, (ID3D11Resource*)_target);
    }
}

/// <summary>
/// CPU path: the full-size 8-bit desktop is read back and scaled by <see cref="BgraDownscaler"/>, with the LUT applied
/// on the CPU (port of the non-hardware path of DxGrabber::captureFrame).
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed unsafe class CpuScaler : IScaler
{
    private readonly GpuDevice _gpu;
    private readonly CaptureRegion _region;
    private readonly int _sourceHeight;
    private readonly bool _toneMapping;
    private ID3D11Texture2D* _staging;

    public CpuScaler(GpuDevice gpu, int sourceWidth, int sourceHeight, CaptureRegion region, bool toneMapping)
    {
        _gpu = gpu;
        _region = region;
        _sourceHeight = sourceHeight;
        _toneMapping = toneMapping;
        _staging = gpu.CreateTexture((uint)sourceWidth, (uint)sourceHeight, DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_USAGE_STAGING, 0, (uint)D3D11_CPU_ACCESS_READ);
    }

    public void CopyFrom(ID3D11Texture2D* desktop) => _gpu.CopyOverlap(_staging, desktop);

    public bool WriteTo(Frame frame, int x, ToneMappingLut? lut)
    {
        if (!_gpu.TryMap(_staging, out var data, out var pitch))
        {
            return false;
        }

        try
        {
            var source = new ReadOnlySpan<byte>(data, checked(pitch * _sourceHeight));
            BgraDownscaler.Downscale(source, pitch, _sourceHeight, _region, _toneMapping ? lut : null, frame, x);
        }
        finally
        {
            _gpu.Unmap(_staging);
        }

        return true;
    }

    public void Dispose() => Com.Release(ref _staging);
}
