using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Backbloom.Core.Audio;
using Backbloom.Core.Color;
using Backbloom.Core.Effects;

namespace Backbloom.Core.Tests.Golden;

/// <summary>
/// Effect fixtures: the ten kept effects frame by frame (C++: tests/golden/EffectsGolden.cpp), stepped the way
/// <c>Effect::run</c> steps them. Tolerance 0: LED colors and pictures match byte for byte.
/// </summary>
/// <remarks>
/// "Candle" and the start hue of "Full color mood blobs" come from the MSVC rand() sequence after srand(seed), which
/// <see cref="MsvcRandom"/> reproduces. The music fixtures feed their audio blocks through <see cref="MusicAnalysis"/>
/// and call the effect four times per block on a cleared 80x45 picture.
/// </remarks>
public sealed class EffectsGoldenTests
{
    public static TheoryData<string> EffectFixtures() => Fixtures.All("effects");

    [Fact]
    public void Catalog_HasTheTenKeptEffectsAndTheNewMusicEffects()
    {
        Assert.Equal(15, EffectCatalog.All.Select(e => e.Name).Distinct().Count());
        Assert.Equal(5, EffectCatalog.All.Count(e => e.Create(new EffectContext(new MsvcRandom(1), null)) is MusicAnimation));
        Assert.All(EffectCatalog.All, e => Assert.EndsWith(".", e.Description, StringComparison.Ordinal));
        foreach (var fixture in EffectFixtures())
        {
            var f = Fixtures.Load(fixture.Data);
            var definition = EffectCatalog.Find(f["name"]!.GetValue<string>());
            Assert.NotNull(definition);
            Assert.Equal(f["smoothingDirectMode"]!.GetValue<bool>(), definition.SmoothingDirect);
            Assert.Equal(f["smoothingTime"]!.GetValue<int>(), definition.SmoothingTimeMs);
            Assert.Equal(f["smoothingFrequency"]!.GetValue<int>(), definition.SmoothingFrequency);
        }
    }

    [Theory]
    [MemberData(nameof(EffectFixtures))]
    public void Effect_MatchesCppReference(string fixture)
    {
        var f = Fixtures.Load(fixture);
        var definition = EffectCatalog.Find(f["name"]!.GetValue<string>())!;
        var diff = new GoldenDiff(fixture, 0);
        if (f["blocks"] is JsonArray blocks)
        {
            CompareMusic(definition, f, blocks, diff);
        }
        else
        {
            CompareAmbient(definition, f, diff);
        }

        diff.AssertNoMismatches();
    }

    private static void CompareAmbient(EffectDefinition definition, JsonNode f, GoldenDiff diff)
    {
        var grid = f["gridSize"]!.AsArray();
        var canvas = new EffectCanvas(grid[0]!.GetValue<int>(), grid[1]!.GetValue<int>());
        var effect = definition.Create(new EffectContext(new MsvcRandom(f["seed"]!.GetValue<uint>()), null));
        effect.Init(canvas, f["latchTime"]!.GetValue<int>());
        var leds = new Rgb8[f["ledCount"]!.GetValue<int>()];
        var frames = f["frames"]!.AsArray();
        var recorded = frames.ToDictionary(n => n!["frame"]!.GetValue<int>(), n => n!);
        var last = recorded.Keys.Max();
        for (var frame = 0; frame <= last; frame++)
        {
            var output = effect.Render(canvas, leds);
            if (!recorded.TryGetValue(frame, out var expected))
            {
                continue;
            }

            var where = $"frame {frame}";
            diff.Equal($"{where} sleep time", expected["sleepTime"]!.GetValue<int>(), Math.Min(effect.SleepTimeMs, Effect.MaxSleepTimeMs));
            if (expected["image"] is { } image)
            {
                diff.Equal($"{where} output", EffectOutput.Image, output);
                CompareImage(where, image, canvas, diff);
            }
            else
            {
                diff.Equal($"{where} output", EffectOutput.Leds, output);
                var actual = MemoryMarshal.AsBytes(leds.AsSpan());
                if (expected["leds"] is { } hex)
                {
                    diff.Bytes($"{where} leds", Convert.FromHexString(hex.GetValue<string>()), actual);
                }
                else
                {
                    var color = Convert.FromHexString(expected["color"]!.GetValue<string>());
                    for (var i = 0; i < leds.Length; i++)
                    {
                        diff.Bytes($"{where} led {i}", color, actual.Slice(i * 3, 3));
                    }
                }
            }
        }
    }

    private static void CompareMusic(EffectDefinition definition, JsonNode f, JsonArray blocks, GoldenDiff diff)
    {
        var music = new MusicAnalysis { Smoothing = true };
        music.Start();
        var canvas = new EffectCanvas();
        var effect = definition.Create(new EffectContext(new MsvcRandom(1), music));
        effect.Init(canvas, Effect.LatchTimeMs);
        diff.Equal("sleep time", f["sleepTime"]!.GetValue<int>(), effect.SleepTimeMs);
        var samples = new short[MusicAnalysis.BlockSize];
        for (var b = 0; b < blocks.Count; b++)
        {
            Convert.FromBase64String(blocks[b]!["audio"]!.GetValue<string>()).AsSpan().CopyTo(MemoryMarshal.AsBytes(samples.AsSpan()));
            Assert.True(music.Analyze(samples, out _));
            var calls = blocks[b]!["frames"]!.AsArray();
            for (var call = 0; call < calls.Count; call++)
            {
                canvas.Clear();
                var output = effect.Render(canvas, []);
                var where = $"block {b} call {call}";
                if (calls[call] is { } image)
                {
                    diff.Equal($"{where} output", EffectOutput.Image, output);
                    CompareImage(where, image, canvas, diff);
                }
                else
                {
                    diff.Equal($"{where} output", EffectOutput.None, output);
                }
            }
        }
    }

    private static void CompareImage(string where, JsonNode image, EffectCanvas canvas, GoldenDiff diff)
    {
        diff.Equal($"{where} width", image["width"]!.GetValue<int>(), canvas.Width);
        diff.Equal($"{where} height", image["height"]!.GetValue<int>(), canvas.Height);
        var expected = Inflate(image["rgbZlib"]!.GetValue<string>());
        Assert.True(image["fnv1a"]!.GetValue<string>() == TestImages.Fnv1a(expected), $"{where}: fixture image checksum mismatch.");
        var actual = new byte[canvas.Width * canvas.Height * 3];
        canvas.CopyRgb(actual);
        diff.Bytes($"{where} pixels", expected, actual);
    }

    private static byte[] Inflate(string base64)
    {
        using var input = new MemoryStream(Convert.FromBase64String(base64));
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
