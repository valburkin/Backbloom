namespace Overglow.Core.Tests.Golden;

/// <summary>
/// HDR tone mapping fixtures (C++: DefaultLut, LutLoader and the system grabber's decodeBGRA, exported by
/// tests/golden/HdrGolden.cpp). The port belongs to Windows capture (port step 3); until then these tests check that
/// the fixtures load and that their inputs can be rebuilt.
/// </summary>
public sealed class HdrGoldenTests
{
    private const string NotPorted = "HDR tone mapping (default LUT, BGRA decoding, GPU shader) is ported with Windows capture in port step 3.";

    [Fact]
    public void DefaultLutFixture_Loads()
    {
        var f = Fixtures.Load("hdr/cpp-default-lut.json");
        var grid = f["grid"]!.AsArray();

        Assert.Equal(256 * 256 * 256 * 3, f["tableSize"]!.GetValue<int>());
        Assert.Matches("^[0-9a-f]{32}$", f["md5"]!.GetValue<string>());
        Assert.Equal(grid.Count * grid.Count * grid.Count * 3, Convert.FromBase64String(f["samples"]!.GetValue<string>()).Length);
        Assert.Equal(256 * 3, Convert.FromBase64String(f["grays"]!.GetValue<string>()).Length);
    }

    [Fact]
    public void DecodeBgraFixture_InputsAreReproducibleAndPlainCopiesSampleTheSource()
    {
        foreach (var c in Fixtures.Load("hdr/cpp-decode-bgra.json")["cases"]!.AsArray())
        {
            var name = c!["name"]!.GetValue<string>();
            var width = c["width"]!.GetValue<int>();
            var height = c["height"]!.GetValue<int>();
            var lineSize = c["lineSize"]!.GetValue<int>();
            var rgb = TestImages.Render(c["image"], width, height);
            var bgra = ToBgra(rgb, width, height, lineSize);
            Assert.True(c["bgraFnv1a"]!.GetValue<string>() == TestImages.Fnv1a(bgra), $"{name}: the rebuilt BGRA input differs from the C++ one.");

            var targetWidth = c["targetWidth"]!.GetValue<int>();
            var targetHeight = c["targetHeight"]!.GetValue<int>();
            var expected = Convert.FromBase64String(c["expectedRgb"]!.GetValue<string>());
            Assert.Equal(targetWidth * targetHeight * 3, expected.Length);
            if (c["lut"]!.GetValue<string>() != "none")
            {
                continue;
            }

            var division = c["division"]!.GetValue<int>();
            var startX = c["startX"]!.GetValue<int>();
            var startY = c["startY"]!.GetValue<int>();
            for (var j = 0; j < targetHeight; j++)
            {
                for (var i = 0; i < targetWidth; i++)
                {
                    var source = ((Math.Min(startY + (j * division), height - 1) * width) + startX + (i * division)) * 3;
                    var target = ((j * targetWidth) + i) * 3;
                    Assert.True(expected.AsSpan(target, 3).SequenceEqual(rgb.AsSpan(source, 3)), $"{name}: pixel ({i}, {j})");
                }
            }
        }
    }

    [Fact(Skip = NotPorted)]
    public void ToneMapping_MatchesCppReference() => Assert.Fail(NotPorted);

    // the C++ test frame: B, G, R, A with A = (x * 31 + y * 17) & 255, row padding 0xEE
    private static byte[] ToBgra(byte[] rgb, int width, int height, int lineSize)
    {
        var bgra = new byte[lineSize * height];
        bgra.AsSpan().Fill(0xEE);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var s = ((y * width) + x) * 3;
                var d = (y * lineSize) + (x * 4);
                bgra[d] = rgb[s + 2];
                bgra[d + 1] = rgb[s + 1];
                bgra[d + 2] = rgb[s];
                bgra[d + 3] = (byte)(((x * 31) + (y * 17)) & 255);
            }
        }

        return bgra;
    }
}
