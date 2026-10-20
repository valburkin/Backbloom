using System.Numerics;
using System.Text.Json.Nodes;

namespace Backbloom.Core.Color;

/// <summary>An 8-bit RGB triple, as stored in the color calibration settings.</summary>
public readonly record struct Rgb8(byte R, byte G, byte B)
{
    public static Rgb8 Black => new(0, 0, 0);

    public static Rgb8 White => new(255, 255, 255);

    public Vector3 ToVector() => new(R / 255.0f, G / 255.0f, B / 255.0f);

    /// <summary>Reads <c>[r, g, b]</c>; returns <paramref name="fallback"/> when the node isn't such an array.</summary>
    public static Rgb8 FromJson(JsonNode? node, Rgb8 fallback)
    {
        if (node is not JsonArray { Count: >= 3 } a)
        {
            return fallback;
        }

        return new Rgb8(Channel(a[0]), Channel(a[1]), Channel(a[2]));

        static byte Channel(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue(out double d) ? (byte)Math.Clamp((int)d, 0, 255) : (byte)0;
    }

    public JsonArray ToJson() => [R, G, B];
}
