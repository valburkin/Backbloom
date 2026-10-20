using System.Numerics;
using Backbloom.Core.Layout;
using Backbloom.Core.Mapping;

namespace Backbloom.Core.Tests.Golden;

/// <summary>
/// Image to LED mapping against <c>fixtures/mapping</c> (C++: ImageColorAveraging, exported by
/// tests/golden/MappingGolden.cpp) and LED layout parsing against <c>fixtures/layouts/cpp-parsing.json</c>.
/// </summary>
/// <remarks>
/// Exact match: sums of 16-bit integers, then one float conversion and two float divisions in the same order as C++.
/// </remarks>
public sealed class MappingGoldenTests
{
    public static TheoryData<string> MappingFixtures() => Fixtures.All("mapping");

    [Theory]
    [MemberData(nameof(MappingFixtures))]
    public void Mapping_MatchesCppReference(string fixture)
    {
        var f = Fixtures.Load(fixture);
        var layout = LedLayout.FromJson(f["leds"]);
        var diff = new GoldenDiff(fixture, f["tolerance"]!.GetValue<float>());
        var cases = f["cases"]!.AsArray();

        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i]!;
            var width = c["width"]!.GetValue<int>();
            var height = c["height"]!.GetValue<int>();
            var mappingType = c["mappingType"]!.GetValue<string>();
            var sparse = c["sparseProcessing"]!.GetValue<bool>();
            var horizontalBorder = c["horizontalBorder"]!.GetValue<int>();
            var verticalBorder = c["verticalBorder"]!.GetValue<int>();
            var label = $"case {i} ({TestImages.Describe(c["image"])} {width}x{height} {mappingType}{(sparse ? " sparse" : "")} " +
                $"borders {horizontalBorder}/{verticalBorder})";

            var frame = TestFrames.FromRgb(width, height, TestImages.RenderChecked(c, label));
            var map = new ImageToLedMap(layout, LedMappingTypes.Parse(mappingType), sparse, width, height, horizontalBorder, verticalBorder);
            var colors = new Vector3[layout.Count];
            map.Process(frame.ReadOnlyPixels, colors);

            var expected = GoldenDiff.Vectors(c["expected"]);
            diff.Equal($"{label} LED count", expected.Length, colors.Length);
            for (var led = 0; led < Math.Min(expected.Length, colors.Length); led++)
            {
                diff.Compare($"{label} LED {led}", expected[led], colors[led]);
            }
        }

        diff.AssertNoMismatches();
    }

    [Fact]
    public void LayoutParsing_MatchesCppReference()
    {
        const string Fixture = "layouts/cpp-parsing.json";
        var diff = new GoldenDiff(Fixture, 0);

        foreach (var c in Fixtures.Load(Fixture)["cases"]!.AsArray())
        {
            var name = c!["name"]!.GetValue<string>();
            var layout = LedLayout.FromJson(c["leds"]);
            var parsed = c["parsed"]!.AsArray();

            diff.Equal($"{name} LED count", parsed.Count, layout.Count);
            for (var i = 0; i < Math.Min(parsed.Count, layout.Count); i++)
            {
                var p = parsed[i]!.AsArray();
                var expected = new LedArea(
                    p[0]!.GetValue<double>(), p[1]!.GetValue<double>(), p[2]!.GetValue<double>(), p[3]!.GetValue<double>(),
                    p[4]!.GetValue<int>(), p[5]!.GetValue<bool>());
                diff.Equal($"{name} LED {i}", expected, layout.Areas[i]);
            }

            diff.Equal($"{name} hasDisabled", c["hasDisabled"]!.GetValue<bool>(), layout.HasDisabled);
            var grid = c["gridSize"]!.AsArray();
            diff.Equal($"{name} grid size", (grid[0]!.GetValue<int>(), grid[1]!.GetValue<int>()), layout.GetGridSize());
        }

        diff.AssertNoMismatches();
    }
}
