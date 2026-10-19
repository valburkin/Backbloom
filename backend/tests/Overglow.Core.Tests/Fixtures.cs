using System.Numerics;
using System.Text.Json.Nodes;
using Overglow.Core.Imaging;

namespace Overglow.Core.Tests;

/// <summary>
/// Golden fixture files under backend/tests/fixtures (copied next to the test binaries). Hand-written fixtures and
/// the ones exported from the C++ reference build share one format per folder (see backend/README.md), so exported
/// files can simply be dropped into the folder and are picked up by the theories.
/// </summary>
internal static class Fixtures
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static JsonNode Load(string relativePath) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Root, relativePath))) ?? throw new InvalidDataException(relativePath);

    /// <summary>Relative paths of every .json file in a fixture folder, for use as theory data.</summary>
    public static TheoryData<string> All(string folder)
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, folder), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.Combine(folder, Path.GetFileName(file)));
        }

        return data;
    }

    public static Vector3 ReadVector(JsonNode? node)
    {
        var a = node?.AsArray() ?? throw new InvalidDataException("Expected [r, g, b].");
        return new Vector3(a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>());
    }

    /// <summary>
    /// Builds a BGRA frame from a fixture <c>frame</c> object: <c>{ width, height, rgb: "hex" }</c> where
    /// <c>rgb</c> is row-major 8-bit RGB (the C++ <c>Image&lt;ColorRgb&gt;</c> memory layout).
    /// </summary>
    public static Frame ReadFrame(JsonNode? node)
    {
        var o = node?.AsObject() ?? throw new InvalidDataException("Expected a frame object.");
        var width = o["width"]!.GetValue<int>();
        var height = o["height"]!.GetValue<int>();
        var rgb = Convert.FromHexString(o["rgb"]!.GetValue<string>().Replace(" ", "", StringComparison.Ordinal));
        if (rgb.Length != width * height * 3)
        {
            throw new InvalidDataException($"Frame data has {rgb.Length} bytes, expected {width * height * 3}.");
        }

        return TestFrames.FromRgb(width, height, rgb);
    }
}

internal static class TestFrames
{
    public static Frame FromRgb(int width, int height, ReadOnlySpan<byte> rgb)
    {
        var frame = new Frame(width, height);
        var px = frame.Pixels;
        for (var i = 0; i < width * height; i++)
        {
            px[(i * 4) + 0] = rgb[(i * 3) + 2];
            px[(i * 4) + 1] = rgb[(i * 3) + 1];
            px[(i * 4) + 2] = rgb[(i * 3) + 0];
            px[(i * 4) + 3] = 255;
        }

        return frame;
    }

    public static Frame Solid(int width, int height, byte r, byte g, byte b)
    {
        var frame = new Frame(width, height);
        var px = frame.Pixels;
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = 255;
        }

        return frame;
    }

    public static void SetPixel(Frame frame, int x, int y, byte r, byte g, byte b)
    {
        var o = (y * frame.Stride) + (x * 4);
        frame.Pixels[o] = b;
        frame.Pixels[o + 1] = g;
        frame.Pixels[o + 2] = r;
        frame.Pixels[o + 3] = 255;
    }
}
