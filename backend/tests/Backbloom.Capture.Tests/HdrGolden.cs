using System.Text.Json.Nodes;
using Backbloom.Capture.Hdr;
using Backbloom.Core.Imaging;
using Backbloom.Core.Tests.Golden;

namespace Backbloom.Capture.Tests;

/// <summary>One case of hdr/cpp-decode-bgra.json (C++ decodeBGRA of the system grabber, tests/golden/HdrGolden.cpp).</summary>
internal sealed record DecodeCase(string Name, int Width, int Height, int LineSize, byte[] Bgra, bool Lut, CaptureRegion Region, byte[] ExpectedRgb)
{
    public static IEnumerable<DecodeCase> Load()
    {
        foreach (var c in HdrFixtures.Load("hdr/cpp-decode-bgra.json")["cases"]!.AsArray())
        {
            var name = c!["name"]!.GetValue<string>();
            var width = c["width"]!.GetValue<int>();
            var height = c["height"]!.GetValue<int>();
            var lineSize = c["lineSize"]!.GetValue<int>();
            var bgra = ToBgra(TestImages.Render(c["image"], width, height), width, height, lineSize);
            if (c["bgraFnv1a"]!.GetValue<string>() != TestImages.Fnv1a(bgra))
            {
                throw new InvalidDataException($"{name}: the rebuilt BGRA input differs from the C++ one.");
            }

            var division = c["division"]!.GetValue<int>();
            var targetWidth = c["targetWidth"]!.GetValue<int>();
            var targetHeight = c["targetHeight"]!.GetValue<int>();
            var region = new CaptureRegion(
                c["startX"]!.GetValue<int>(), c["startY"]!.GetValue<int>(), targetWidth * division, targetHeight * division, division, targetWidth, targetHeight);
            yield return new DecodeCase(name, width, height, lineSize, bgra, c["lut"]!.GetValue<string>() != "none", region, Convert.FromBase64String(c["expectedRgb"]!.GetValue<string>()));
        }
    }

    /// <summary>RGB24 of a BGRA frame, for comparing with the C++ output.</summary>
    public static byte[] ToRgb(Frame frame)
    {
        var rgb = new byte[frame.Width * frame.Height * 3];
        var p = frame.ReadOnlyPixels;
        for (int i = 0, o = 0; o < rgb.Length; i += 4, o += 3)
        {
            (rgb[o], rgb[o + 1], rgb[o + 2]) = (p[i + 2], p[i + 1], p[i]);
        }

        return rgb;
    }

    // The C++ test frame: B, G, R, A with A = (x * 31 + y * 17) & 255, row padding 0xEE.
    private static byte[] ToBgra(byte[] rgb, int width, int height, int lineSize)
    {
        var bgra = new byte[lineSize * height];
        bgra.AsSpan().Fill(0xEE);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var s = ((y * width) + x) * 3;
                var d = (y * lineSize) + (x * 4);
                bgra[d] = rgb[s + 2];
                bgra[d + 1] = rgb[s + 1];
                bgra[d + 2] = rgb[s];
                bgra[d + 3] = (byte)(((x * 31) + (y * 17)) & 255);
            }
        }

        return bgra;
    }
}

internal static class HdrFixtures
{
    private static readonly Lazy<ToneMappingLut> s_defaultLut = new(() => new ToneMappingLut(DefaultLut.Create(), "built-in default"));

    /// <summary>The default table, built once per test process.</summary>
    public static ToneMappingLut DefaultTable => s_defaultLut.Value;

    public static JsonNode Load(string relativePath) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", relativePath))) ??
        throw new InvalidDataException($"Empty fixture {relativePath}.");
}
