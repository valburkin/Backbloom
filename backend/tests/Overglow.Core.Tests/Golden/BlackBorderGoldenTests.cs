using Overglow.Core.Borders;

namespace Overglow.Core.Tests.Golden;

/// <summary>
/// Black border detection against <c>fixtures/blackborder/cpp-detector.json</c> (C++: BlackBorderDetector, exported
/// by tests/golden/BlackBorderGolden.cpp). Integer math: exact match.
/// </summary>
public sealed class BlackBorderGoldenTests
{
    private const string Fixture = "blackborder/cpp-detector.json";

    [Fact]
    public void Detector_MatchesCppReference()
    {
        var f = Fixtures.Load(Fixture);
        var diff = new GoldenDiff(Fixture, 0);

        foreach (var t in f["calculateThreshold"]!.AsArray())
        {
            var pair = t!.AsArray();
            var threshold = pair[0]!.GetValue<double>();
            diff.Equal($"calculateThreshold({threshold})", pair[1]!.GetValue<int>(), (int)BlackBorderDetector.CalculateThreshold(threshold));
        }

        foreach (var c in f["cases"]!.AsArray())
        {
            var width = c!["width"]!.GetValue<int>();
            var height = c["height"]!.GetValue<int>();
            var label = $"{c["name"]} {width}x{height}";
            var frame = TestFrames.FromRgb(width, height, TestImages.RenderChecked(c, label));

            foreach (var r in c["results"]!.AsArray())
            {
                var threshold = r!["threshold"]!.GetValue<double>();
                var detector = new BlackBorderDetector(threshold);
                foreach (var mode in Enum.GetValues<BlackBorderMode>())
                {
                    var e = r[mode.ToSettingString()]!;
                    var expected = new BlackBorder(e["unknown"]!.GetValue<bool>(), e["horizontalSize"]!.GetValue<int>(), e["verticalSize"]!.GetValue<int>());
                    var actual = detector.Process(frame, mode);

                    // compare the raw values: BlackBorder equality treats all unknown borders as equal
                    diff.Equal(
                        $"{label} threshold {threshold} {mode.ToSettingString()}",
                        (expected.IsUnknown, expected.HorizontalSize, expected.VerticalSize),
                        (actual.IsUnknown, actual.HorizontalSize, actual.VerticalSize));
                }
            }
        }

        diff.AssertNoMismatches();
    }
}
