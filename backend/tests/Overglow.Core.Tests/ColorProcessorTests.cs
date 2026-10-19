using System.Numerics;
using Overglow.Core.Color;

namespace Overglow.Core.Tests;

public sealed class ColorProcessorTests
{
    // InfiniteProcessing::test() compares with this tolerance.
    private const float CppTolerance = 0.008f;

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance = CppTolerance) =>
        Assert.True(
            MathF.Abs(expected.X - actual.X) < tolerance && MathF.Abs(expected.Y - actual.Y) < tolerance &&
            MathF.Abs(expected.Z - actual.Z) < tolerance,
            $"expected {expected}, got {actual}");

    private static ColorProcessor Processor(ColorAdjustment adjustment) => new(adjustment);

    [Theory]
    [MemberData(nameof(ColorFixtures))]
    public void Golden_MatchesFixture(string fixture)
    {
        var f = Fixtures.Load(fixture);
        var processor = new ColorProcessor(ColorAdjustment.FromJson(f["adjustment"]));
        var tolerance = f["tolerance"]!.GetValue<float>();
        var step = f["step"]!.GetValue<string>();

        foreach (var c in f["cases"]!.AsArray())
        {
            var color = Fixtures.ReadVector(c!["input"]);
            switch (step)
            {
                case "calibrate":
                    processor.Calibrate(ref color);
                    break;
                case "process":
                    var one = new[] { color };
                    processor.Process(one);
                    color = one[0];
                    break;
                default:
                    throw new InvalidDataException($"Unknown step '{step}' in {fixture}.");
            }

            AssertClose(Fixtures.ReadVector(c["expected"]), color, tolerance);
        }
    }

    public static TheoryData<string> ColorFixtures() => Fixtures.All("color");

    // ---- step tests ported from InfiniteProcessing::test() ----

    [Fact]
    public void Srgb_RoundTripsMidGray()
    {
        AssertClose(new Vector3(0.214f), Srgb.ToLinear(new Vector3(0.5f)));
        AssertClose(new Vector3(0.5f), Srgb.ToNonlinear(new Vector3(0.214f)));
    }

    [Fact]
    public void UserGamma_1_5()
    {
        var color = new Vector3(0.708f, 0.332f, 0.1f);
        Processor(new ColorAdjustment { Gamma = 1.5f }).ApplyUserGamma(ref color);
        AssertClose(new Vector3(0.596f, 0.191f, 0.032f), color);
    }

    [Fact]
    public void Temperature_Cold()
    {
        var color = new Vector3(0.8f, 0.5f, 0.2f);
        Processor(new ColorAdjustment { Temperature = TemperaturePreset.Cold }).ApplyTemperature(ref color);
        AssertClose(new Vector3(0.720f, 0.475f, 0.200f), color);
    }

    [Fact]
    public void LuminanceAndSaturation()
    {
        var color = new Vector3(1.0f, 0.5f, 0.0f);
        Processor(new ColorAdjustment { LuminanceGain = 0.8f, SaturationGain = 1.2f }).ApplyLuminanceAndSaturation(ref color);
        AssertClose(new Vector3(0.800f, 0.400f, 0.000f), color);
    }

    [Fact]
    public void Calibrate_Matrix_SwapsRedAndBlue()
    {
        var color = new Vector3(1.0f, 0.5f, 0.0f);
        Processor(new ColorAdjustment { Red = new Rgb8(0, 0, 255), Green = new Rgb8(0, 255, 0), Blue = new Rgb8(255, 0, 0) }).Calibrate(ref color);
        AssertClose(new Vector3(0.0f, 0.5f, 1.0f), color);
    }

    [Fact]
    public void Calibrate_Lut_RedTarget()
    {
        var color = new Vector3(1.0f, 0.0f, 0.0f);
        Processor(new ColorAdjustment { ClassicConfig = false, Red = new Rgb8(250, 5, 5) }).Calibrate(ref color);
        AssertClose(new Vector3(0.980f, 0.020f, 0.020f), color);
    }

    // ---- C++ "pipeline" tests: they chain the steps in their own order (not Process()'s order) ----

    private static Vector3 CppPipeline(ColorProcessor p, Vector3 color)
    {
        color = Srgb.ToLinear(color);
        p.Calibrate(ref color);
        p.ApplyTemperature(ref color);
        p.ApplyLuminanceAndSaturation(ref color);
        color = Srgb.ToNonlinear(color);
        p.ApplyUserGamma(ref color);
        return color;
    }

    [Fact]
    public void CppPipeline_Original()
    {
        var p = Processor(new ColorAdjustment
        {
            Red = new Rgb8(240, 10, 10),
            Green = new Rgb8(10, 240, 10),
            Blue = new Rgb8(10, 10, 240),
            Temperature = TemperaturePreset.Warm,
            LuminanceGain = 0.8f,
            SaturationGain = 1.2f,
            Gamma = 1.5f,
        });
        AssertClose(new Vector3(0.595f, 0.191f, 0.000f), CppPipeline(p, new Vector3(0.8f, 0.4f, 0.1f)));
    }

    [Fact]
    public void CppPipeline_ColdImage()
    {
        var p = Processor(new ColorAdjustment { Temperature = TemperaturePreset.Cold, LuminanceGain = 0.7f, SaturationGain = 1.0f, Gamma = 1.1f });
        AssertClose(new Vector3(0.451f, 0.464f, 0.476f), CppPipeline(p, new Vector3(0.6f)));
    }

    [Fact]
    public void CppPipeline_HighContrast()
    {
        var p = Processor(new ColorAdjustment
        {
            Red = new Rgb8(255, 10, 10),
            Green = new Rgb8(10, 255, 10),
            Blue = new Rgb8(10, 10, 255),
            LuminanceGain = 1.1f,
            SaturationGain = 1.4f,
            Gamma = 1.0f,
        });
        AssertClose(new Vector3(0.940f, 0.000f, 0.000f), CppPipeline(p, new Vector3(0.9f, 0.2f, 0.2f)));
    }

    [Fact]
    public void CppPipeline_NoOperation()
    {
        var p = Processor(new ColorAdjustment { Gamma = 1.0f });
        AssertClose(new Vector3(0.7f, 0.5f, 0.3f), CppPipeline(p, new Vector3(0.7f, 0.5f, 0.3f)));
    }

    [Fact]
    public void CppPipeline_MatrixInversion()
    {
        var p = Processor(new ColorAdjustment { Red = new Rgb8(0, 255, 0), Green = new Rgb8(0, 0, 255), Blue = new Rgb8(255, 0, 0), Gamma = 1.0f });
        AssertClose(new Vector3(0.500f, 0.200f, 0.800f), CppPipeline(p, new Vector3(0.8f, 0.5f, 0.2f)));
    }

    [Fact]
    public void CppPipeline_SepiaLut()
    {
        var sepia = new Rgb8(112, 66, 20);
        var p = Processor(new ColorAdjustment
        {
            ClassicConfig = false,
            Red = sepia,
            Green = sepia,
            Blue = sepia,
            Cyan = Rgb8.Black,
            Magenta = Rgb8.Black,
            Yellow = Rgb8.Black,
            White = new Rgb8(255, 240, 192),
            SaturationGain = 0.8f,
            Gamma = 1.0f,
        });
        AssertClose(new Vector3(0.556f, 0.492f, 0.401f), CppPipeline(p, new Vector3(0.7f)));
    }

    [Fact]
    public void CppPipeline_GreenBoost()
    {
        var p = Processor(new ColorAdjustment
        {
            Red = new Rgb8(235, 0, 0),
            Green = new Rgb8(0, 255, 0),
            Blue = new Rgb8(0, 0, 235),
            Temperature = TemperaturePreset.Warm,
            Gamma = 1.0f,
        });
        AssertClose(new Vector3(0.385f, 0.775f, 0.356f), CppPipeline(p, new Vector3(0.4f, 0.8f, 0.4f)));
    }

    [Fact]
    public void CppPipeline_PastelLut()
    {
        var p = Processor(new ColorAdjustment
        {
            ClassicConfig = false,
            Red = new Rgb8(255, 128, 128),
            Green = new Rgb8(128, 255, 128),
            Blue = new Rgb8(128, 128, 255),
            Cyan = Rgb8.Black,
            Magenta = new Rgb8(255, 128, 255),
            Gamma = 0.9f,
        });
        AssertClose(new Vector3(1.0f, 0.760f, 1.0f), CppPipeline(p, new Vector3(1.0f, 0.0f, 1.0f)));
    }

    // ---- Process() as a whole, hand-computed ----

    [Fact]
    public void Process_ScaleOutputHalvesLinearLightBeforeGamma()
    {
        var colors = new[] { Vector3.One };
        Processor(new ColorAdjustment { ScaleOutput = 0.5f, Gamma = 1.0f }).Process(colors);

        // sRGB encoding of linear 0.5 is 0.7354.
        AssertClose(new Vector3(0.7354f), colors[0], 0.002f);
    }

    [Fact]
    public void Process_DefaultGammaIsAppliedAfterEncoding()
    {
        var colors = new[] { Vector3.One, Vector3.Zero, new Vector3(0.214f) };
        Processor(ColorAdjustment.Default).Process(colors);

        AssertClose(Vector3.One, colors[0], 1e-6f);
        AssertClose(Vector3.Zero, colors[1], 1e-6f);
        AssertClose(new Vector3(MathF.Pow(0.5f, 1.5f)), colors[2], 0.003f);
    }

    [Fact]
    public void Process_PowerLimitScalesTheWholeStrip()
    {
        // Total output 3 of 6; allowed 6 * 0.25 = 1.5, so everything is halved.
        var colors = new[] { Vector3.One, Vector3.Zero };
        Processor(new ColorAdjustment { PowerLimit = 0.25f, Gamma = 1.0f }).Process(colors);

        AssertClose(new Vector3(0.5f), colors[0], 1e-6f);
        AssertClose(Vector3.Zero, colors[1], 1e-6f);
    }

    [Fact]
    public void Process_PowerLimitLeavesDimFramesAlone()
    {
        var colors = new[] { new Vector3(0.2f), Vector3.Zero };
        Processor(new ColorAdjustment { PowerLimit = 0.5f, Gamma = 1.0f }).Process(colors);

        AssertClose(Srgb.ToNonlinear(new Vector3(0.2f)), colors[0], 1e-6f);
    }

    [Fact]
    public void Process_ColoredBacklightRaisesEachChannel()
    {
        var colors = new[] { new Vector3(0, 0.5f, 0) };
        Processor(new ColorAdjustment { BacklightThreshold = 0.1f, BacklightColored = true, Gamma = 1.0f }).Process(colors);

        AssertClose(new Vector3(0.1f, Srgb.ToNonlinear(new Vector3(0.5f)).Y, 0.1f), colors[0], 1e-6f);
    }

    [Fact]
    public void Process_WhiteBacklightOnlyBelowThreshold()
    {
        var colors = new[] { Vector3.Zero, Vector3.One };
        Processor(new ColorAdjustment { BacklightThreshold = 0.1f, BacklightColored = false, Gamma = 1.0f }).Process(colors);

        AssertClose(new Vector3(0.1f), colors[0], 1e-6f);
        AssertClose(Vector3.One, colors[1], 1e-6f);
    }

    [Fact]
    public void Process_BacklightAtOrAboveOneFallsBackToMinimalLevel()
    {
        var colors = new[] { Vector3.Zero };
        Processor(new ColorAdjustment { BacklightThreshold = 1.0f, Gamma = 1.0f }).Process(colors);

        AssertClose(new Vector3(0.0039f), colors[0], 1e-6f);
    }

    [Fact]
    public void Process_DisabledOnlyEncodes()
    {
        var colors = new[] { new Vector3(0.214f) };
        new ColorProcessor(new ColorAdjustment { ScaleOutput = 0.1f }, enabled: false).Process(colors);

        AssertClose(new Vector3(0.5f), colors[0], 0.002f);
    }

    [Fact]
    public void FromJson_ReadsChannelAdjustmentEntry()
    {
        var a = ColorAdjustment.FromJson(System.Text.Json.Nodes.JsonNode.Parse("""
            { "temperatureSetting": "custom", "temperatureRed": 0.5, "classic_config": false, "red": [250, 1, 2],
              "scaleOutput": 0.8, "gamma": 2.2, "luminanceGain": 1.1, "saturationGain": 0.9, "backlightThreshold": 0.0039,
              "backlightColored": false, "powerLimit": 0.7 }
            """));

        Assert.Equal(TemperaturePreset.Custom, a.Temperature);
        Assert.Equal(new Vector3(0.5f, 1, 1), a.CustomTemperature);
        Assert.False(a.ClassicConfig);
        Assert.Equal(new Rgb8(250, 1, 2), a.Red);
        Assert.Equal(new Rgb8(0, 255, 0), a.Green);
        Assert.Equal(0.8f, a.ScaleOutput);
        Assert.Equal(2.2f, a.Gamma);
        Assert.Equal(1.1f, a.LuminanceGain);
        Assert.Equal(0.9f, a.SaturationGain);
        Assert.Equal(0.0039f, a.BacklightThreshold);
        Assert.False(a.BacklightColored);
        Assert.Equal(0.7f, a.PowerLimit);
    }
}
