using System.Numerics;
using Overglow.Core.Color;

namespace Overglow.Core.Tests.Golden;

/// <summary>
/// Color processing against <c>fixtures/color</c> (C++: InfiniteProcessing, exported by tests/golden/ColorGolden.cpp).
/// </summary>
/// <remarks>
/// The exported fixtures expect an exact match: both sides build the same float lookup tables (powf from the same
/// C runtime) and run the same float operations in the same order. Hand-written fixtures state their own tolerance.
/// </remarks>
public sealed class ColorGoldenTests
{
    public static TheoryData<string> ColorFixtures() => Fixtures.All("color");

    [Theory]
    [MemberData(nameof(ColorFixtures))]
    public void Color_MatchesCppReference(string fixture)
    {
        var f = Fixtures.Load(fixture);
        var processor = new ColorProcessor(ColorAdjustment.FromJson(f["adjustment"]), f["enabled"]?.GetValue<bool>() ?? true);
        var order = ColorOrders.Parse(f["colorOrder"]?.GetValue<string>());
        var step = f["step"]!.GetValue<string>();
        var inputs = GoldenDiff.Vectors(f["inputs"]);
        var expected = GoldenDiff.Vectors(f["expected"]);
        var diff = new GoldenDiff(fixture, f["tolerance"]!.GetValue<float>());
        var colors = (Vector3[])inputs.Clone();

        switch (step)
        {
            case "process":
                // the inputs are one strip (the power limit sees all of them)
                processor.Process(colors);
                break;
            case "calibrate":
                for (var i = 0; i < colors.Length; i++)
                {
                    processor.Calibrate(ref colors[i]);
                }

                break;
            default:
                throw new InvalidDataException($"Unknown step '{step}' in {fixture}.");
        }

        diff.Equal("color count", expected.Length, colors.Length);
        for (var i = 0; i < Math.Min(expected.Length, colors.Length); i++)
        {
            // the C++ processor applies the device color order itself; here it is the quantizer's job
            diff.Compare($"input {i} {inputs[i]}", expected[i], InColorOrder(colors[i], order));
        }

        if (f["expectedBytes"] is { } expectedBytes)
        {
            var bytes = new byte[colors.Length * 3];
            ColorQuantizer.ToBytes(colors, order, bytes);
            diff.Bytes("bytes", Convert.FromHexString(expectedBytes.GetValue<string>()), bytes);
        }

        diff.AssertNoMismatches();
    }

    private static Vector3 InColorOrder(Vector3 c, ColorOrder order) => order switch
    {
        ColorOrder.Bgr => new Vector3(c.Z, c.Y, c.X),
        ColorOrder.Rbg => new Vector3(c.X, c.Z, c.Y),
        ColorOrder.Grb => new Vector3(c.Y, c.X, c.Z),
        ColorOrder.Gbr => new Vector3(c.Y, c.Z, c.X),
        ColorOrder.Brg => new Vector3(c.Z, c.X, c.Y),
        _ => c,
    };
}
