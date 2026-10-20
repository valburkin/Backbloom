using System.Text.Json.Nodes;
using Backbloom.Core.Imaging;

namespace Backbloom.Core.Tests;

/// <summary>
/// Golden fixture files under backend/tests/fixtures (copied next to the test binaries): exported from the C++
/// reference by tests/golden (cpp-*.json) or hand-written (hand-*.json), one format per folder (see backend/README.md);
/// the theories pick up every file of a folder.
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
