using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Capture.Hdr;
using ZstdSharp;

namespace Overglow.Capture.Tests;

/// <summary>The default table is built once for all tests of this class (about a second on all cores).</summary>
public sealed class DefaultTableFixture
{
    public byte[] Table { get; } = HdrFixtures.DefaultTable.Table.ToArray();
}

public sealed class LutTests(DefaultTableFixture fixture) : IClassFixture<DefaultTableFixture>, IDisposable
{
    /// <summary>MD5 of the HDR RGB table HyperHDR v22 created as its default LUT (tests/unit/HdrLutTest.cpp).</summary>
    private const string DefaultTableMd5 = "51da2e6ff577303ec3b010f9d296f2d6";

    private readonly string _dir = Directory.CreateTempSubdirectory("overglow-lut-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Index_RedChangesFastest()
    {
        Assert.Equal(0, ToneMappingLut.Index(0, 0, 0));
        Assert.Equal(3, ToneMappingLut.Index(1, 0, 0));
        Assert.Equal(256 * 3, ToneMappingLut.Index(0, 1, 0));
        Assert.Equal(65536 * 3, ToneMappingLut.Index(0, 0, 1));
        Assert.Equal(ToneMappingLut.TableSize - 3, ToneMappingLut.Index(255, 255, 255));
    }

    [Fact]
    public void DefaultTable_IsBitIdenticalToTheCppTable()
    {
        Assert.Equal(DefaultTableMd5, Md5(fixture.Table));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(32, 32, 32, 9, 9, 9)]
    [InlineData(64, 64, 64, 43, 43, 43)]
    [InlineData(128, 128, 128, 176, 176, 176)]
    [InlineData(160, 160, 160, 255, 255, 255)]
    [InlineData(200, 0, 0, 255, 0, 0)]
    public void DefaultTable_MapsPqSamples(byte r, byte g, byte b, byte er, byte eg, byte eb)
    {
        var lut = new ToneMappingLut(fixture.Table, "test");
        Assert.Equal((er, eg, eb), lut.Map(r, g, b));
    }

    [Fact]
    public void Loader_AcceptsOneAndThreeTableFiles_AndUsesTheFirstTable()
    {
        var single = Path.Combine(_dir, "single.3d");
        var legacy = Path.Combine(_dir, "legacy.3d");
        File.WriteAllBytes(single, fixture.Table);
        using (var file = File.Create(legacy))
        {
            file.Write(fixture.Table);
            var filler = new byte[ToneMappingLut.TableSize];
            Array.Fill(filler, (byte)0x7f);
            file.Write(filler);
            file.Write(filler);
        }

        foreach (var path in new[] { single, legacy })
        {
            var lut = LutLoader.Load([Path.Combine(_dir, "missing.3d"), path], NullLogger.Instance);
            Assert.NotNull(lut);
            Assert.Equal(path, lut.Source);
            Assert.Equal(DefaultTableMd5, Md5(lut.Table));
        }
    }

    [Fact]
    public void Loader_ReadsTheCompressedVariant()
    {
        var path = Path.Combine(_dir, LutLoader.FileName);
        using var compressor = new Compressor(1);
        File.WriteAllBytes(path + LutLoader.CompressedSuffix, compressor.Wrap(fixture.Table).ToArray());

        var lut = LutLoader.Load([path], NullLogger.Instance);

        Assert.NotNull(lut);
        Assert.Equal(DefaultTableMd5, Md5(lut.Table));
    }

    [Fact]
    public void Loader_RejectsInvalidFiles_AndStopsAtTheFirstExistingOne()
    {
        var truncated = Path.Combine(_dir, "truncated.3d");
        var valid = Path.Combine(_dir, "valid.3d");
        File.WriteAllBytes(truncated, fixture.Table.AsSpan(0, 1000).ToArray());
        File.WriteAllBytes(valid, fixture.Table);

        Assert.Null(LutLoader.Load([truncated, valid], NullLogger.Instance));
        Assert.Null(LutLoader.Load([Path.Combine(_dir, "missing.3d")], NullLogger.Instance));
    }

    [Fact]
    public void Locations_SearchTheDataDirectoryFirst_ThenTheAppDirectory()
    {
        var locations = LutLocations.Default(_dir);
        Assert.Equal([Path.Combine(_dir, LutLoader.FileName), Path.Combine(AppContext.BaseDirectory, LutLoader.FileName)], locations.Files);
        Assert.Equal(_dir, locations.CreateDirectory);
    }

    [Fact]
    public void Provider_PrefersAnInstalledFile()
    {
        var custom = new byte[ToneMappingLut.TableSize];
        custom[3] = 42;
        var appDir = Path.Combine(_dir, "app");
        Directory.CreateDirectory(appDir);
        File.WriteAllBytes(Path.Combine(appDir, LutLoader.FileName), custom);
        var provider = new LutProvider(new LutLocations([Path.Combine(_dir, "data"), appDir], Path.Combine(_dir, "data")), NullLogger<LutProvider>.Instance);

        var lut = provider.Get();

        Assert.NotNull(lut);
        Assert.Equal((42, 0, 0), lut.Map(1, 0, 0));
        Assert.Same(lut, provider.TryGet());
        Assert.False(File.Exists(Path.Combine(_dir, "data", LutLoader.FileName)));
    }

    [Fact]
    public void Provider_CreatesAndSavesTheDefaultTable_WhenNoFileExists()
    {
        var data = Path.Combine(_dir, "data");
        var provider = new LutProvider(new LutLocations([data], data), NullLogger<LutProvider>.Instance);

        var lut = provider.Get();

        Assert.NotNull(lut);
        Assert.Equal(DefaultTableMd5, Md5(lut.Table));
        Assert.Equal(DefaultTableMd5, Md5(File.ReadAllBytes(Path.Combine(data, LutLoader.FileName))));
        Assert.False(File.Exists(Path.Combine(data, LutLoader.FileName + ".tmp")));
    }

    // MD5 only identifies the table (the C++ test publishes that digest); it isn't used for security.
#pragma warning disable CA5351
    private static string Md5(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(MD5.HashData(data));
#pragma warning restore CA5351
}
