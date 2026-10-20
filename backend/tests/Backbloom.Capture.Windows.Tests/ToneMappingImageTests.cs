using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Backbloom.Capture.Hdr;
using Backbloom.Core.Imaging;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace Backbloom.Capture.Windows.Tests;

/// <summary>
/// Renders a synthetic HDR test card through the capture conversions and saves the results as PNG files for a visual
/// check (only with BACKBLOOM_CAPTURE_DUMP_DIR): an scRGB desktop as Windows HDR delivers it, converted with SDR white
/// at 200 nits and without the conversion (1.0 = white, as an SDR pipeline would read it), and the same scene as an
/// HDR10 (PQ, BT.2020) signal in 8 bits with and without the LUT.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed unsafe class ToneMappingImageTests
{
    private const int Width = 3440;
    private const int Height = 1440;

    [Fact]
    public void DumpToneMappedTestCard()
    {
        var dir = Environment.GetEnvironmentVariable("BACKBLOOM_CAPTURE_DUMP_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "set BACKBLOOM_CAPTURE_DUMP_DIR to save the images");
        Directory.CreateDirectory(dir!);
        using var gpu = GpuDevice.CreateWarp();
        gpu.CreateShaders();
        var region = CaptureRegion.Compute(Width, Height, default, 512);
        var lut = new ToneMappingLut(DefaultLut.Create(), "built-in default");

        var scRgb = new Half[Width * Height * 4];
        var pq = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var nits = Scene(x / (double)Width, y / (double)Height);
                var i = ((y * Width) + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    scRgb[i + c] = (Half)(nits[c] / 80.0);
                }

                scRgb[i + 3] = (Half)1;
                var bt2020 = ToBt2020(nits);
                pq[i] = Pq(bt2020[2]);
                pq[i + 1] = Pq(bt2020[1]);
                pq[i + 2] = Pq(bt2020[0]);
                pq[i + 3] = 255;
            }
        }

        var fp16 = MemoryMarshal.AsBytes(scRgb.AsSpan()).ToArray();
        Save(dir!, "hdr-scrgb-200nits", Run(gpu, fp16, DXGI_FORMAT_R16G16B16A16_FLOAT, 8, region, wideGamut: true, nits: 200, lut: null));
        Save(dir!, "hdr-scrgb-off", Run(gpu, fp16, DXGI_FORMAT_R16G16B16A16_FLOAT, 8, region, wideGamut: true, nits: 80, lut: null));
        Save(dir!, "hdr10-8bit-lut", Run(gpu, pq, DXGI_FORMAT_B8G8R8A8_UNORM, 4, region, wideGamut: false, nits: 200, lut: lut));
        Save(dir!, "hdr10-8bit-off", Run(gpu, pq, DXGI_FORMAT_B8G8R8A8_UNORM, 4, region, wideGamut: false, nits: 200, lut: null));
    }

    /// <summary>
    /// Linear BT.709 light in nits: a sky gradient, a sun highlight of 1000 nits, a skin tone, saturated primaries at
    /// SDR white, a gray ramp from 0 to 400 nits and dark shadows.
    /// </summary>
    private static double[] Scene(double u, double v)
    {
        if (v > 0.8)
        {
            var gray = u * 400;
            return [gray, gray, gray];
        }

        if (v > 0.55)
        {
            double[][] patches = [[200, 30, 25], [40, 180, 40], [25, 40, 200], [150, 105, 80], [200, 200, 200], [12, 12, 12], [200, 160, 20]];
            return patches[Math.Min((int)(u * patches.Length), patches.Length - 1)];
        }

        var dx = u - 0.75;
        var dy = (v - 0.2) * 0.42;
        if ((dx * dx) + (dy * dy) < 0.003)
        {
            return [1000, 950, 800];
        }

        var sky = 1 - (v / 0.55);
        return [40 + (60 * sky), 70 + (90 * sky), 130 + (120 * sky)];
    }

    private static double[] ToBt2020(double[] bt709) =>
    [
        (0.6274 * bt709[0]) + (0.3293 * bt709[1]) + (0.0433 * bt709[2]),
        (0.0691 * bt709[0]) + (0.9195 * bt709[1]) + (0.0114 * bt709[2]),
        (0.0164 * bt709[0]) + (0.0880 * bt709[1]) + (0.8956 * bt709[2]),
    ];

    /// <summary>PQ encoding of a luminance in nits, as 8 bits.</summary>
    private static byte Pq(double nits)
    {
        const double M1 = 2610.0 / 16384.0, M2 = 2523.0 / 4096.0 * 128.0, C1 = 3424.0 / 4096.0, C2 = 2413.0 / 4096.0 * 32.0, C3 = 2392.0 / 4096.0 * 32.0;
        var y = Math.Pow(Math.Clamp(nits / 10000.0, 0, 1), M1);
        return (byte)Math.Round(255 * Math.Pow((C1 + (C2 * y)) / (1 + (C3 * y)), M2));
    }

    private static Frame Run(GpuDevice gpu, byte[] data, DXGI_FORMAT format, int bytesPerPixel, CaptureRegion region, bool wideGamut, int nits, ToneMappingLut? lut)
    {
        ID3D11Texture2D* texture;
        fixed (byte* p = data)
        {
            texture = gpu.CreateTexture(Width, Height, format, D3D11_USAGE_DEFAULT, (uint)D3D11_BIND_SHADER_RESOURCE, 0, p, (uint)(Width * bytesPerPixel));
        }

        try
        {
            using var scaler = new GpuScaler(gpu, Width, Height, region, format, wideGamut, nits, toneMapping: lut is not null);
            var frame = new Frame(region.TargetWidth, region.TargetHeight);
            scaler.CopyFrom(texture);
            Assert.True(scaler.WriteTo(frame, 0, lut));
            return frame;
        }
        finally
        {
            Com.Release(ref texture);
        }
    }

    private static void Save(string dir, string name, Frame frame) => Png.Write(Path.Combine(dir, $"capture-frame-{name}.png"), frame);
}
