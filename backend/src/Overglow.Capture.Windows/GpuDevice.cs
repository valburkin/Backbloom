using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Overglow.Capture.Hdr;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D_DRIVER_TYPE;
using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.D3D11_CREATE_DEVICE_FLAG;
using static TerraFX.Interop.DirectX.D3D11_FILTER;
using static TerraFX.Interop.DirectX.D3D11_TEXTURE_ADDRESS_MODE;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace Overglow.Capture.Windows;

/// <summary>
/// The Direct3D 11 device of one adapter and what all of its monitors share: the scaling shaders, the sampler and the
/// tone mapping LUT as a 3D texture (created when first needed). Port of the device part of DxGrabber::initDirectX and
/// DxGrabber::initShaders.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed unsafe class GpuDevice : IDisposable
{
    private static readonly byte[] s_vertexShader = LoadShader("Downscale.vs.cso");
    private static readonly byte[] s_pixelShader = LoadShader("Downscale.ps.cso");

    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private ID3D11VertexShader* _vertexShader;
    private ID3D11PixelShader* _pixelShader;
    private ID3D11SamplerState* _sampler;
    private ID3D11ShaderResourceView* _lutView;
    private ToneMappingLut? _lut;

    private GpuDevice(ID3D11Device* device, ID3D11DeviceContext* context)
    {
        _device = device;
        _context = context;
    }

    public ID3D11Device* Device => _device;

    public ID3D11DeviceContext* Context => _context;

    /// <summary>True when the scaling shaders are available (hardware acceleration on and created successfully).</summary>
    public bool HasShaders => _pixelShader is not null;

    public ID3D11VertexShader* VertexShader => _vertexShader;

    public ID3D11PixelShader* PixelShader => _pixelShader;

    public ID3D11SamplerState* Sampler => _sampler;

    public static GpuDevice Create(IDXGIAdapter1* adapter)
    {
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;
        Com.Check(
            D3D11CreateDevice((IDXGIAdapter*)adapter, D3D_DRIVER_TYPE_UNKNOWN, HMODULE.NULL, (uint)D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, 0, D3D11.D3D11_SDK_VERSION, &device, null, &context),
            "D3D11CreateDevice");
        return new GpuDevice(device, context);
    }

    /// <summary>Creates the shaders and the sampler; on failure the device is still usable for the CPU path.</summary>
    public void CreateShaders()
    {
        fixed (byte* vs = s_vertexShader)
        fixed (byte* ps = s_pixelShader)
        {
            ID3D11VertexShader* vertexShader = null;
            ID3D11PixelShader* pixelShader = null;
            ID3D11SamplerState* sampler = null;
            try
            {
                Com.Check(_device->CreateVertexShader(vs, (nuint)s_vertexShader.Length, null, &vertexShader), "CreateVertexShader");
                Com.Check(_device->CreatePixelShader(ps, (nuint)s_pixelShader.Length, null, &pixelShader), "CreatePixelShader");
                var samplerDesc = new D3D11_SAMPLER_DESC
                {
                    Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                    AddressU = D3D11_TEXTURE_ADDRESS_CLAMP,
                    AddressV = D3D11_TEXTURE_ADDRESS_CLAMP,
                    AddressW = D3D11_TEXTURE_ADDRESS_CLAMP,
                    ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
                    MaxLOD = D3D11.D3D11_FLOAT32_MAX,
                };
                Com.Check(_device->CreateSamplerState(&samplerDesc, &sampler), "CreateSamplerState");
                _vertexShader = vertexShader;
                _pixelShader = pixelShader;
                _sampler = sampler;
            }
            catch
            {
                Com.Release(ref vertexShader);
                Com.Release(ref pixelShader);
                Com.Release(ref sampler);
                throw;
            }
        }
    }

    /// <summary>
    /// The LUT as a 256x256x256 RGBA8 3D texture (64 MB of video memory), sampled with exact texel loads so the GPU
    /// result is bit-identical to the CPU table. Created once per device and table.
    /// </summary>
    public ID3D11ShaderResourceView* GetLutView(ToneMappingLut lut)
    {
        if (ReferenceEquals(_lut, lut))
        {
            return _lutView;
        }

        Com.Release(ref _lutView);
        _lut = null;
        const int Size = 256;
        var rgba = (byte*)NativeMemory.Alloc(Size * Size * Size * 4);
        ID3D11Texture3D* texture = null;
        try
        {
            var table = lut.Table;
            for (int i = 0, o = 0; i < ToneMappingLut.TableSize; i += 3, o += 4)
            {
                rgba[o] = table[i];
                rgba[o + 1] = table[i + 1];
                rgba[o + 2] = table[i + 2];
                rgba[o + 3] = 255;
            }

            var desc = new D3D11_TEXTURE3D_DESC
            {
                Width = Size,
                Height = Size,
                Depth = Size,
                MipLevels = 1,
                Format = DXGI_FORMAT_R8G8B8A8_UNORM,
                Usage = D3D11_USAGE_IMMUTABLE,
                BindFlags = (uint)D3D11_BIND_SHADER_RESOURCE,
            };
            var data = new D3D11_SUBRESOURCE_DATA { pSysMem = rgba, SysMemPitch = Size * 4, SysMemSlicePitch = Size * Size * 4 };
            Com.Check(_device->CreateTexture3D(&desc, &data, &texture), "CreateTexture3D (LUT)");
            ID3D11ShaderResourceView* view = null;
            Com.Check(_device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "CreateShaderResourceView (LUT)");
            _lutView = view;
            _lut = lut;
            return view;
        }
        finally
        {
            Com.Release(ref texture);
            NativeMemory.Free(rgba);
        }
    }

    /// <summary>A software (WARP) device, for tests without a GPU or desktop.</summary>
    public static GpuDevice CreateWarp()
    {
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;
        Com.Check(
            D3D11CreateDevice(null, D3D_DRIVER_TYPE_WARP, HMODULE.NULL, (uint)D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, 0, D3D11.D3D11_SDK_VERSION, &device, null, &context),
            "D3D11CreateDevice (WARP)");
        return new GpuDevice(device, context);
    }

    /// <summary>Why the device stopped working, or S_OK.</summary>
    public HRESULT RemovedReason => _device->GetDeviceRemovedReason();

    public ID3D11Texture2D* CreateTexture(uint width, uint height, DXGI_FORMAT format, D3D11_USAGE usage, uint bind, uint cpuAccess, void* initialData = null, uint rowPitch = 0)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = usage,
            BindFlags = bind,
            CPUAccessFlags = cpuAccess,
        };
        var data = new D3D11_SUBRESOURCE_DATA { pSysMem = initialData, SysMemPitch = rowPitch };
        ID3D11Texture2D* texture = null;
        Com.Check(_device->CreateTexture2D(&desc, initialData is null ? null : &data, &texture), "CreateTexture2D");
        return texture;
    }

    /// <summary>Copies the area both textures cover (sizes only differ for rotated monitors).</summary>
    public void CopyOverlap(ID3D11Texture2D* destination, ID3D11Texture2D* source)
    {
        D3D11_TEXTURE2D_DESC sourceDesc, destinationDesc;
        source->GetDesc(&sourceDesc);
        destination->GetDesc(&destinationDesc);
        if (sourceDesc.Width == destinationDesc.Width && sourceDesc.Height == destinationDesc.Height)
        {
            _context->CopyResource((ID3D11Resource*)destination, (ID3D11Resource*)source);
            return;
        }

        var box = new D3D11_BOX
        {
            right = Math.Min(sourceDesc.Width, destinationDesc.Width),
            bottom = Math.Min(sourceDesc.Height, destinationDesc.Height),
            back = 1,
        };
        _context->CopySubresourceRegion((ID3D11Resource*)destination, 0, 0, 0, 0, (ID3D11Resource*)source, 0, &box);
    }

    /// <summary>Maps a staging texture for reading (waits for the GPU); false when the device is gone.</summary>
    public bool TryMap(ID3D11Texture2D* staging, out byte* data, out int rowPitch)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        if (_context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped).FAILED)
        {
            data = null;
            rowPitch = 0;
            return false;
        }

        data = (byte*)mapped.pData;
        rowPitch = (int)mapped.RowPitch;
        return true;
    }

    public void Unmap(ID3D11Texture2D* staging) => _context->Unmap((ID3D11Resource*)staging, 0);

    public void Dispose()
    {
        Com.Release(ref _lutView);
        Com.Release(ref _sampler);
        Com.Release(ref _pixelShader);
        Com.Release(ref _vertexShader);
        if (_context is not null)
        {
            _context->ClearState();
        }

        Com.Release(ref _context);
        Com.Release(ref _device);
        _lut = null;
    }

    private static byte[] LoadShader(string name)
    {
        var resource = "Overglow.Capture.Windows.Shaders." + name;
        using var stream = typeof(GpuDevice).Assembly.GetManifestResourceStream(resource) ??
            throw new InvalidOperationException($"Missing shader bytecode {resource}.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
