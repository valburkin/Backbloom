namespace Overglow.Core.Tests.Golden;

/// <summary>
/// RGBW fixtures (C++: RGBW::rgb2rgbw and InfiniteColorEngineRgbw, exported by tests/golden/ColorGolden.cpp). The RGBW
/// encoders belong to the drivers that send RGBW (DDP, Art-Net, Adalight, file) and are ported with the remaining
/// drivers in port step 6; until then these tests check the fixtures' shape and the trivial "white_off" case.
/// </summary>
public sealed class RgbwGoldenTests
{
    private const string NotPorted = "The RGBW encoders are ported with the remaining LED drivers in port step 6.";

    [Fact]
    public void ClassicFixture_IsComplete()
    {
        var f = Fixtures.Load("rgbw/cpp-classic.json");
        var inputs = Convert.FromHexString(f["inputs"]!.GetValue<string>());

        foreach (var c in f["cases"]!.AsArray())
        {
            var output = Convert.FromHexString(c!["expectedRgbw"]!.GetValue<string>());
            Assert.Equal(inputs.Length / 3 * 4, output.Length);
            if (c["algorithm"]!.GetValue<string>() == "white_off")
            {
                for (var i = 0; i < inputs.Length / 3; i++)
                {
                    Assert.Equal([inputs[i * 3], inputs[(i * 3) + 1], inputs[(i * 3) + 2], 0], output.AsSpan(i * 4, 4).ToArray());
                }
            }
        }
    }

    [Fact]
    public void InfiniteEngineFixture_IsComplete()
    {
        var f = Fixtures.Load("rgbw/cpp-infinite-engine.json");
        var frames = f["frames"]!.AsArray();

        foreach (var c in f["cases"]!.AsArray())
        {
            var outputs = c!["expectedRgbw"]!.AsArray();
            Assert.Equal(frames.Count, outputs.Count);
            for (var i = 0; i < frames.Count; i++)
            {
                Assert.Equal(frames[i]!.AsArray().Count * 4, Convert.FromHexString(outputs[i]!.GetValue<string>()).Length);
            }
        }
    }

    [Fact(Skip = NotPorted)]
    public void Encoders_MatchCppReference() => Assert.Fail(NotPorted);
}
