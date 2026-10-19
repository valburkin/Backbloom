using System.Text.Json.Nodes;

namespace Overglow.Core.Tests.Golden;

/// <summary>
/// Rebuilds the synthetic test images the golden fixtures describe by spec (C++: tests/golden/TestImages.cpp). Same
/// integer math, so the pixels are identical; fixtures carry an FNV-1a checksum to prove it.
/// </summary>
internal static class TestImages
{
    /// <summary>Renders a spec into row-major RGB24 (the C++ <c>Image&lt;ColorRgb&gt;</c> layout).</summary>
    public static byte[] Render(JsonNode? spec, int width, int height)
    {
        var s = spec?.AsObject() ?? throw new InvalidDataException("Expected an image spec.");
        var kind = s["kind"]!.GetValue<string>();
        var rgb = new byte[width * height * 3];

        switch (kind)
        {
            case "overlay":
                Render(s["base"], width, height).CopyTo(rgb, 0);
                foreach (var item in s["rects"]!.AsArray())
                {
                    var r = item!.AsArray();
                    var color = Color(r[4]);
                    var x0 = Math.Max(r[0]!.GetValue<int>(), 0);
                    var y0 = Math.Max(r[1]!.GetValue<int>(), 0);
                    var x1 = Math.Min(r[0]!.GetValue<int>() + r[2]!.GetValue<int>(), width);
                    var y1 = Math.Min(r[1]!.GetValue<int>() + r[3]!.GetValue<int>(), height);
                    for (var y = y0; y < y1; y++)
                    {
                        for (var x = x0; x < x1; x++)
                        {
                            Set(rgb, width, x, y, color);
                        }
                    }
                }

                return rgb;

            case "hex":
                var data = Convert.FromHexString(s["rgb"]!.GetValue<string>().Replace(" ", "", StringComparison.Ordinal));
                data.AsSpan(0, Math.Min(data.Length, rgb.Length)).CopyTo(rgb);
                return rgb;

            default:
                break;
        }

        var state = (uint)(s["seed"]?.GetValue<double>() ?? 0);
        var from = s["from"] is { } f ? Color(f) : default;
        var to = s["to"] is { } t ? Color(t) : default;
        var colors = s["colors"]?.AsArray().Select(Color).ToArray() ?? [];
        var cell = Math.Max(s["cell"]?.GetValue<int>() ?? 1, 1);
        var solid = s["color"] is { } c ? Color(c) : default;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = kind switch
                {
                    "solid" => solid,
                    "hgradient" => Mix(from, to, x, width - 1),
                    "vgradient" => Mix(from, to, y, height - 1),
                    "quadrants" => colors[(y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1)],
                    "checker" => colors[(((x / cell) + (y / cell)) % 2 == 0) ? 0 : 1],
                    "random" => (Next(ref state), Next(ref state), Next(ref state)),
                    _ => throw new InvalidDataException($"Unknown image kind '{kind}'."),
                };
                Set(rgb, width, x, y, pixel);
            }
        }

        return rgb;
    }

    /// <summary>FNV-1a (32 bit) as 8 lowercase hex digits, like golden::fnv1a.</summary>
    public static string Fnv1a(ReadOnlySpan<byte> data)
    {
        var hash = 2166136261u;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Renders a fixture case's image and checks it against the case's checksum.</summary>
    public static byte[] RenderChecked(JsonNode testCase, string label)
    {
        var width = testCase["width"]!.GetValue<int>();
        var height = testCase["height"]!.GetValue<int>();
        var rgb = Render(testCase["image"], width, height);
        if (testCase["fnv1a"] is { } checksum)
        {
            Assert.True(
                checksum.GetValue<string>() == Fnv1a(rgb),
                $"{label}: the rebuilt test image differs from the C++ one (FNV-1a {Fnv1a(rgb)}, expected {checksum}); fix TestImages first.");
        }

        return rgb;
    }

    /// <summary>A short description of an image spec for failure messages.</summary>
    public static string Describe(JsonNode? spec)
    {
        var s = spec!.AsObject();
        var kind = s["kind"]!.GetValue<string>();
        return kind switch
        {
            "random" => $"random seed {s["seed"]}",
            "solid" => $"solid {s["color"]!.ToJsonString()}",
            "checker" => $"checker {s["cell"]}",
            "overlay" => $"{Describe(s["base"])} + {s["rects"]!.AsArray().Count} rects",
            _ => kind,
        };
    }

    private static (byte R, byte G, byte B) Color(JsonNode? node)
    {
        var a = node!.AsArray();
        return ((byte)a[0]!.GetValue<int>(), (byte)a[1]!.GetValue<int>(), (byte)a[2]!.GetValue<int>());
    }

    private static (byte R, byte G, byte B) Mix((byte R, byte G, byte B) from, (byte R, byte G, byte B) to, int position, int range)
    {
        range = Math.Max(range, 1);
        return (
            (byte)(from.R + ((to.R - from.R) * position / range)),
            (byte)(from.G + ((to.G - from.G) * position / range)),
            (byte)(from.B + ((to.B - from.B) * position / range)));
    }

    private static byte Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (byte)(state >> 24);
    }

    private static void Set(byte[] rgb, int width, int x, int y, (byte R, byte G, byte B) color)
    {
        var o = ((y * width) + x) * 3;
        rgb[o] = color.R;
        rgb[o + 1] = color.G;
        rgb[o + 2] = color.B;
    }
}
