using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Overglow.Capture.Hdr;
using Overglow.Capture.Tests;
using Overglow.Core.Imaging;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace Overglow.Capture.Windows.Tests;

/// <summary>
/// The scaling shader and the CPU path on a software (WARP) device with synthetic desktop images, so they run without
/// a GPU or a desktop.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed unsafe class ScalerTests : IDisposable
{
    private readonly GpuDevice _gpu;

    public ScalerTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763), "Direct3D 11 needs Windows 10 1809 or later");
        _gpu = GpuDevice.CreateWarp();
        _gpu.CreateShaders();
    }

    public void Dispose() => _gpu.Dispose();

    [Fact]
    public void Sdr_WithoutScaling_CopiesThePixels()
    {
        var bgra = RandomBgra(64, 36, seed: 1);
        var frame = RunGpu(bgra, 64, 36, DXGI_FORMAT_B8G8R8A8_UNORM, CaptureRegion.Compute(64, 36, default, 64), wideGamut: false, lut: null);
        Assert.Equal(Opaque(bgra), frame.ReadOnlyPixels.ToArray());
    }

    [Fact]
    public void Sdr_AveragesEachBlock()
    {
        // 16x16 source scaled by 4: each output pixel is the mean of a 4x4 block.
        var bgra = RandomBgra(16, 16, seed: 2);
        var region = CaptureRegion.Compute(16, 16, default, 4);
        Assert.Equal(4, region.Divide);

        var frame = RunGpu(bgra, 16, 16, DXGI_FORMAT_B8G8R8A8_UNORM, region, wideGamut: false, lut: null);

        for (var oy = 0; oy < 4; oy++)
        {
            for (var ox = 0; ox < 4; ox++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    for (var y = 0; y < 4; y++)
                    {
                        for (var x = 0; x < 4; x++)
                        {
                            sum += bgra[(((((oy * 4) + y) * 16) + (ox * 4) + x) * 4) + c];
                        }
                    }

                    Assert.InRange(frame.ReadOnlyPixels[(((oy * 4) + ox) * 4) + c], (sum / 16.0) - 1, (sum / 16.0) + 1);
                }
            }
        }
    }

    [Fact]
    public void Sdr_StartsAtTheCropOrigin()
    {
        var bgra = RandomBgra(40, 30, seed: 3);
        var region = CaptureRegion.Compute(40, 30, new CropMargins(3, 2, 5, 1), 40);
        var frame = RunGpu(bgra, 40, 30, DXGI_FORMAT_B8G8R8A8_UNORM, region, wideGamut: false, lut: null);

        var expected = new Frame(region.TargetWidth, region.TargetHeight);
        BgraDownscaler.Downscale(bgra, 40 * 4, 30, region, null, expected);
        Assert.Equal(expected.ReadOnlyPixels.ToArray(), frame.ReadOnlyPixels.ToArray());
    }

    /// <summary>
    /// The C++ decodeBGRA fixtures on the GPU. Without scaling the shader looks every pixel up in the LUT 3D texture
    /// with exact texel loads, so the result equals the C++ output byte for byte. With scaling the C++ code took one
    /// pixel per block while the shader averages the looked-up colors of the block (up to 8 x 8 texels); that average
    /// is recomputed here from the same table and must match within 1 level (GPU float rounding).
    /// </summary>
    [Fact]
    public void Lut_OnTheGpu_MatchesTheCppTableMath()
    {
        var lut = HdrFixtures.DefaultTable;
        foreach (var c in DecodeCase.Load())
        {
            var frame = RunGpu(c.Bgra, c.Width, c.Height, DXGI_FORMAT_B8G8R8A8_UNORM, c.Region, wideGamut: false, c.Lut ? lut : null, pitch: c.LineSize);
            var actual = DecodeCase.ToRgb(frame);
            if (c.Region.Divide == 1)
            {
                Assert.True(actual.AsSpan().SequenceEqual(c.ExpectedRgb), $"{c.Name}: differs from the C++ output");
                continue;
            }

            if (!c.Lut)
            {
                continue;
            }

            var d = c.Region.Divide;
            var sum = new int[3];
            for (var j = 0; j < c.Region.TargetHeight; j++)
            {
                for (var i = 0; i < c.Region.TargetWidth; i++)
                {
                    Array.Clear(sum);
                    for (var y = 0; y < d; y++)
                    {
                        for (var x = 0; x < d; x++)
                        {
                            var o = ((c.Region.Y + (j * d) + y) * c.LineSize) + ((c.Region.X + (i * d) + x) * 4);
                            (byte r, byte g, byte b) = lut.Map(c.Bgra[o + 2], c.Bgra[o + 1], c.Bgra[o]);
                            sum[0] += r;
                            sum[1] += g;
                            sum[2] += b;
                        }
                    }

                    for (var k = 0; k < 3; k++)
                    {
                        var mean = sum[k] / (double)(d * d);
                        var value = actual[(((j * c.Region.TargetWidth) + i) * 3) + k];
                        Assert.True(Math.Abs(value - mean) <= 1, $"{c.Name}: pixel ({i}, {j}) channel {k}: {value} vs mean {mean:0.00}");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(0.0, 200, 0)]
    [InlineData(2.5, 200, 255)]
    [InlineData(1.25, 200, 180)]
    [InlineData(0.25, 200, 74)]
    [InlineData(0.01, 200, 5)]
    [InlineData(5.0, 400, 255)]
    [InlineData(1.25, 100, 255)]
    public void ScRgb_ScalesSdrWhiteToOne_AndAppliesTheBt709Curve(double value, int nits, byte expected)
    {
        // scRGB: 1.0 is 80 nits. Expected values: BT.709 OETF of value * 80 / nits.
        var half = new Half[16 * 8 * 4];
        for (var i = 0; i < half.Length; i += 4)
        {
            (half[i], half[i + 1], half[i + 2], half[i + 3]) = ((Half)value, (Half)value, (Half)value, (Half)1);
        }

        var bytes = MemoryMarshal.AsBytes(half.AsSpan()).ToArray();
        var region = CaptureRegion.Compute(16, 8, default, 8);

        var frame = RunGpu(bytes, 16, 8, DXGI_FORMAT_R16G16B16A16_FLOAT, region, wideGamut: true, lut: null, nits);

        Assert.Equal(Rec709(value * 80 / nits), expected);
        Assert.InRange(frame.ReadOnlyPixels[0], expected - 1, expected + 1);
        Assert.Equal(frame.ReadOnlyPixels[0], frame.ReadOnlyPixels[2]);
    }

    [Fact]
    public void ScRgb_OutOfGamutColors_AreClipped()
    {
        Half[] pixel = [(Half)(-0.2), (Half)0.5, (Half)4.0, (Half)1];
        var bytes = MemoryMarshal.AsBytes(pixel.AsSpan()).ToArray();
        var frame = RunGpu(bytes, 1, 1, DXGI_FORMAT_R16G16B16A16_FLOAT, CaptureRegion.Compute(1, 1, default, 1), wideGamut: true, lut: null, 200);

        // Stored as BGRA: blue is 4.0 (clipped), green 0.5 * 0.4, red -0.2 (clipped to 0).
        Assert.Equal([255, Rec709(0.2), 0, 255], frame.ReadOnlyPixels.ToArray());
    }

    [Fact]
    public void Cpu_Path_UsesTheDownscaler()
    {
        var bgra = RandomBgra(64, 36, seed: 5);
        var region = CaptureRegion.Compute(64, 36, new CropMargins(4, 0, 2, 0), 20);
        var expected = new Frame(region.TargetWidth, region.TargetHeight);
        BgraDownscaler.Downscale(bgra, 64 * 4, 36, region, null, expected);

        var frame = RunCpu(bgra, 64, 36, region, null);

        Assert.Equal(expected.ReadOnlyPixels.ToArray(), frame.ReadOnlyPixels.ToArray());
    }

    [Fact]
    public void Scalers_DoNotAllocatePerFrame()
    {
        var bgra = RandomBgra(64, 36, seed: 6);
        var region = CaptureRegion.Compute(64, 36, default, 32);
        var frame = new Frame(region.TargetWidth, region.TargetHeight);
        var desktop = Upload(bgra, 64, 36, DXGI_FORMAT_B8G8R8A8_UNORM, 64 * 4);
        try
        {
            using var gpu = new GpuScaler(_gpu, 64, 36, region, DXGI_FORMAT_B8G8R8A8_UNORM, wideGamut: false, 200, toneMapping: false);
            using var cpu = new CpuScaler(_gpu, 64, 36, region, toneMapping: false);
            void RunFrame()
            {
                gpu.CopyFrom(desktop);
                Assert.True(gpu.WriteTo(frame, 0, null));
                cpu.CopyFrom(desktop);
                Assert.True(cpu.WriteTo(frame, 0, null));
            }

            RunFrame();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 50; i++)
            {
                RunFrame();
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            Com.Release(ref desktop);
        }
    }

    private static byte Rec709(double linear)
    {
        var l = Math.Clamp(linear, 0, 1);
        var v = l < 0.018 ? 4.5 * l : (1.099 * Math.Pow(l, 0.45)) - 0.099;
        return (byte)Math.Round(v * 255);
    }

    private static byte[] RandomBgra(int width, int height, int seed)
    {
        var bytes = new byte[width * height * 4];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Opaque(byte[] bgra)
    {
        var copy = (byte[])bgra.Clone();
        for (var i = 3; i < copy.Length; i += 4)
        {
            copy[i] = 255;
        }

        return copy;
    }

    private Frame RunGpu(byte[] data, int width, int height, DXGI_FORMAT format, CaptureRegion region, bool wideGamut, ToneMappingLut? lut, int nits = 200, int pitch = 0)
    {
        var bytesPerPixel = format == DXGI_FORMAT_R16G16B16A16_FLOAT ? 8 : 4;
        var desktop = Upload(data, width, height, format, pitch > 0 ? pitch : width * bytesPerPixel);
        try
        {
            using var scaler = new GpuScaler(_gpu, width, height, region, format, wideGamut, nits, toneMapping: lut is not null);
            var frame = new Frame(region.TargetWidth, region.TargetHeight);
            scaler.CopyFrom(desktop);
            Assert.True(scaler.WriteTo(frame, 0, lut));
            return frame;
        }
        finally
        {
            Com.Release(ref desktop);
        }
    }

    private Frame RunCpu(byte[] bgra, int width, int height, CaptureRegion region, ToneMappingLut? lut)
    {
        var desktop = Upload(bgra, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, width * 4);
        try
        {
            using var scaler = new CpuScaler(_gpu, width, height, region, toneMapping: lut is not null);
            var frame = new Frame(region.TargetWidth, region.TargetHeight);
            scaler.CopyFrom(desktop);
            Assert.True(scaler.WriteTo(frame, 0, lut));
            return frame;
        }
        finally
        {
            Com.Release(ref desktop);
        }
    }

    private ID3D11Texture2D* Upload(byte[] data, int width, int height, DXGI_FORMAT format, int pitch)
    {
        fixed (byte* p = data)
        {
            return _gpu.CreateTexture((uint)width, (uint)height, format, D3D11_USAGE_DEFAULT, (uint)D3D11_BIND_SHADER_RESOURCE, 0, p, (uint)pitch);
        }
    }
}
