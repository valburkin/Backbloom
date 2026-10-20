using System.Numerics;
using System.Text.Json.Nodes;
using Overglow.Core.Color;
using Overglow.Devices.Rgbw;

namespace Overglow.Devices.Tests;

/// <summary>
/// The RGBW encoders against the fixtures exported from the C++ reference (RGBW::rgb2rgbw and
/// InfiniteColorEngineRgbw, tests/golden/ColorGolden.cpp): every byte must match.
/// </summary>
public sealed class RgbwGoldenTests
{
    private static JsonNode Load(string path) =>
        JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", path)))!;

    public static TheoryData<string> ClassicAlgorithms()
    {
        var data = new TheoryData<string>();
        foreach (var c in Load("rgbw/cpp-classic.json")["cases"]!.AsArray())
        {
            data.Add(c!["algorithm"]!.GetValue<string>());
        }

        return data;
    }

    public static TheoryData<string> EngineCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Load("rgbw/cpp-infinite-engine.json")["cases"]!.AsArray())
        {
            data.Add(c!["name"]!.GetValue<string>());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ClassicAlgorithms))]
    public void Classic_MatchesCppReference(string algorithm)
    {
        var f = Load("rgbw/cpp-classic.json");
        var inputs = Convert.FromHexString(f["inputs"]!.GetValue<string>());
        var expected = Convert.FromHexString(f["cases"]!.AsArray().Single(c => c!["algorithm"]!.GetValue<string>() == algorithm)!["expectedRgbw"]!.GetValue<string>());

        // hyperserial_custom uses gain 200, red 180, green 170, blue 120 in the fixture
        var converter = new RgbwConverter(RgbwConverter.ParseAlgorithm(algorithm), 200, 180, 170, 120);
        var actual = new byte[inputs.Length / 3 * 4];
        converter.Convert(inputs, actual);

        AssertBytes(expected, actual, 4);
    }

    [Theory]
    [MemberData(nameof(EngineCases))]
    public void InfiniteEngine_MatchesCppReference(string name)
    {
        var f = Load("rgbw/cpp-infinite-engine.json");
        var frames = f["frames"]!.AsArray().Select(frame => frame!.AsArray().Select(c => new Vector3(c![0]!.GetValue<float>(), c[1]!.GetValue<float>(), c[2]!.GetValue<float>())).ToArray()).ToList();
        var setup = f["cases"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!;
        var wp = setup["whitePoint"]!.AsArray();
        var options = new InfiniteRgbwOptions(
            true,
            setup["whiteMixerThreshold"]!.GetValue<float>(),
            setup["whiteLedIntensity"]!.GetValue<float>(),
            new Vector3(wp[0]!.GetValue<float>(), wp[1]!.GetValue<float>(), wp[2]!.GetValue<float>()));
        var order = ColorOrders.Parse(setup["colorOrder"]!.GetValue<string>());
        var interval = setup["interval"]!.GetValue<int>();
        var outputs = setup["expectedRgbw"]!.AsArray();
        var encoder = new InfiniteRgbwEncoder();

        for (var i = 0; i < frames.Count; i++)
        {
            var actual = new byte[frames[i].Length * 4];
            encoder.Render(frames[i], interval, options, order, actual);
            AssertBytes(Convert.FromHexString(outputs[i]!.GetValue<string>()), actual, 4, $"frame {i}");
        }
    }

    [Fact]
    public void InfiniteEngine_DoesNotAllocatePerFrame()
    {
        var encoder = new InfiniteRgbwEncoder();
        var colors = Enumerable.Range(0, 168).Select(i => new Vector3(i / 168f, 0.5f, 1 - (i / 168f))).ToArray();
        var output = new byte[168 * 4];
        var options = new InfiniteRgbwOptions(true, 0.1f, 1.8f, new Vector3(1, 0.9f, 0.8f));
        encoder.Render(colors, 20, options, ColorOrder.Grb, output);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            colors[i % 168].X = (i % 7) / 7f;
            encoder.Render(colors, 20, options, ColorOrder.Grb, output);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Options_ReadTheDriverSettings()
    {
        var config = Ctx.Json("""
            { "rgbw": true, "white_algorithm": "hyperserial_custom", "white_channel_limit": 50, "white_channel_red": 300,
              "enable_ice_rgbw": true, "ice_white_mixer_threshold": 0.25, "ice_white_led_intensity": 2.0, "ice_white_temperatur_blue": 0.5 }
            """);

        Assert.Equal(128, RgbwConverter.WhiteChannelLimit(config)); // qRound(50 * 255 / 100 = 127.5)
        Assert.Equal(WhiteAlgorithm.HyperserialCustom, RgbwConverter.FromConfig(config)!.Algorithm);
        Assert.Equal(new InfiniteRgbwOptions(true, 0.25f, 2.0f, new Vector3(1, 1, 0.5f)), InfiniteRgbwOptions.FromConfig(config));
        Assert.Null(RgbwConverter.FromConfig(Ctx.Json("{}")));
        Assert.Throws<ArgumentException>(() => RgbwConverter.FromConfig(Ctx.Json("""{ "rgbw": true, "white_algorithm": "bogus" }""")));
    }

    private static void AssertBytes(byte[] expected, byte[] actual, int stride, string where = "")
    {
        Assert.Equal(expected.Length, actual.Length);
        var mismatches = Enumerable.Range(0, expected.Length / stride)
            .Where(i => !expected.AsSpan(i * stride, stride).SequenceEqual(actual.AsSpan(i * stride, stride)))
            .Take(10)
            .Select(i => $"LED {i}: expected {Convert.ToHexString(expected, i * stride, stride)}, got {Convert.ToHexString(actual, i * stride, stride)}")
            .ToList();
        Assert.True(mismatches.Count == 0, $"{where} {string.Join("; ", mismatches)}");
    }
}
