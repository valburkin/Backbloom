using System.IO.Compression;

namespace Overglow.Core.Tests.Golden;

/// <summary>
/// Effect fixtures: the ten kept effects frame by frame (C++: tests/golden/EffectsGolden.cpp). The effects are ported
/// in port step 5; until then these tests check that every recorded frame decodes.
/// </summary>
/// <remarks>
/// "Candle" and the start hue of "Full color mood blobs" come from the MSVC rand() sequence after srand(seed)
/// (state = state * 214013 + 2531011, rand() = (state &gt;&gt; 16) &amp; 0x7fff); a port has to use that generator to match them.
/// </remarks>
public sealed class EffectsGoldenTests
{
    private const string NotPorted = "Effects (six ambient, four music effects) are ported in port step 5.";

    public static TheoryData<string> EffectFixtures() => Fixtures.All("effects");

    [Theory]
    [MemberData(nameof(EffectFixtures))]
    public void Fixture_FramesDecode(string fixture)
    {
        var f = Fixtures.Load(fixture);
        var frames = 0;

        if (f["blocks"] is { } blocks)
        {
            foreach (var block in blocks.AsArray())
            {
                Assert.Equal(1024 * 2, Convert.FromBase64String(block!["audio"]!.GetValue<string>()).Length);
                foreach (var image in block["frames"]!.AsArray())
                {
                    if (image is not null)
                    {
                        CheckImage(fixture, image);
                        frames++;
                    }
                }
            }
        }
        else
        {
            var ledCount = f["ledCount"]!.GetValue<int>();
            foreach (var frame in f["frames"]!.AsArray())
            {
                Assert.InRange(frame!["sleepTime"]!.GetValue<int>(), 1, 100);
                if (frame["image"] is { } image)
                {
                    CheckImage(fixture, image);
                }
                else if (frame["leds"] is { } leds)
                {
                    Assert.Equal(ledCount * 3, Convert.FromHexString(leds.GetValue<string>()).Length);
                }
                else
                {
                    Assert.Equal(3, Convert.FromHexString(frame["color"]!.GetValue<string>()).Length);
                }

                frames++;
            }
        }

        Assert.True(frames > 0, $"{fixture} has no frames.");
    }

    [Fact(Skip = NotPorted)]
    public void Effects_MatchCppReference() => Assert.Fail(NotPorted);

    private static void CheckImage(string fixture, System.Text.Json.Nodes.JsonNode image)
    {
        using var input = new MemoryStream(Convert.FromBase64String(image["rgbZlib"]!.GetValue<string>()));
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        var rgb = output.ToArray();

        Assert.Equal(image["width"]!.GetValue<int>() * image["height"]!.GetValue<int>() * 3, rgb.Length);
        Assert.True(image["fnv1a"]!.GetValue<string>() == TestImages.Fnv1a(rgb), $"{fixture}: image checksum mismatch.");
    }
}
