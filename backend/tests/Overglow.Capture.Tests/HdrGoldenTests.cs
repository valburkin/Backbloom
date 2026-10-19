using System.Security.Cryptography;
using Overglow.Capture.Hdr;
using Overglow.Core.Imaging;

namespace Overglow.Capture.Tests;

/// <summary>
/// HDR tone mapping against the fixtures exported from the C++ reference (tests/golden/HdrGolden.cpp): the default
/// table (DefaultLut::fillTable) and the CPU capture path (decodeBGRA with and without the table). Tolerance 0.
/// </summary>
public sealed class HdrGoldenTests
{
    [Fact]
    public void DefaultTable_MatchesTheCppTable()
    {
        var f = HdrFixtures.Load("hdr/cpp-default-lut.json");
        var lut = HdrFixtures.DefaultTable;
        Assert.Equal(ToneMappingLut.TableSize, f["tableSize"]!.GetValue<int>());

        // CA5351: MD5 only identifies the table, as in the C++ test.
#pragma warning disable CA5351
        Assert.Equal(f["md5"]!.GetValue<string>(), Convert.ToHexStringLower(MD5.HashData(lut.Table)));
#pragma warning restore CA5351

        var grid = f["grid"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
        var samples = Convert.FromBase64String(f["samples"]!.GetValue<string>());
        var i = 0;
        foreach (var b in grid)
        {
            foreach (var g in grid)
            {
                foreach (var r in grid)
                {
                    Assert.True(
                        lut.Table.Slice(ToneMappingLut.Index(r, g, b), 3).SequenceEqual(samples.AsSpan(i, 3)),
                        $"entry ({r}, {g}, {b})");
                    i += 3;
                }
            }
        }

        var grays = Convert.FromBase64String(f["grays"]!.GetValue<string>());
        for (var v = 0; v < 256; v++)
        {
            Assert.True(lut.Table.Slice(ToneMappingLut.Index(v, v, v), 3).SequenceEqual(grays.AsSpan(v * 3, 3)), $"gray {v}");
        }
    }

    [Fact]
    public void CpuPath_MatchesCppDecodeBgra()
    {
        var cases = DecodeCase.Load().ToList();
        Assert.Equal(6, cases.Count);
        foreach (var c in cases)
        {
            var frame = new Frame(c.Region.TargetWidth, c.Region.TargetHeight);

            BgraDownscaler.Downscale(c.Bgra, c.LineSize, c.Height, c.Region, c.Lut ? HdrFixtures.DefaultTable : null, frame);

            var actual = DecodeCase.ToRgb(frame);
            var mismatch = Enumerable.Range(0, actual.Length).FirstOrDefault(k => actual[k] != c.ExpectedRgb[k], -1);
            Assert.True(mismatch < 0, $"{c.Name}: pixel {mismatch / 3} differs");
        }
    }
}
