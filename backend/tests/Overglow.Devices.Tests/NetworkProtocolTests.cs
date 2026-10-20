using System.Numerics;
using Overglow.Core.Color;
using Overglow.Devices.Drivers.ArtNet;
using Overglow.Devices.Drivers.Ddp;
using Overglow.Devices.Drivers.E131;
using Overglow.Devices.Rgbw;

namespace Overglow.Devices.Tests;

/// <summary>Byte-exact E1.31, Art-Net and DDP (RGBW) packets, derived from the C++ packet builders.</summary>
public sealed class NetworkProtocolTests
{
    private static readonly Guid s_cid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    private static byte[] Leds(int count) => [.. Enumerable.Range(0, count * 3).Select(i => (byte)(i + 1))];

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static byte[] E131Header(int channels, byte sequence, int universe, string source = "Test")
    {
        var name = new byte[64];
        System.Text.Encoding.UTF8.GetBytes(source).CopyTo(name, 0);
        var root = 0x7000 | (110 + channels);
        var frame = 0x7000 | (88 + channels);
        var dmp = 0x7000 | (11 + channels);
        return Concat(
            [0x00, 0x10, 0x00, 0x00, 0x41, 0x53, 0x43, 0x2d, 0x45, 0x31, 0x2e, 0x31, 0x37, 0x00, 0x00, 0x00],
            [(byte)(root >> 8), (byte)root, 0, 0, 0, 4],
            [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff],
            [(byte)(frame >> 8), (byte)frame, 0, 0, 0, 2],
            name,
            [100, 0, 0, sequence, 0, (byte)(universe >> 8), (byte)universe],
            [(byte)(dmp >> 8), (byte)dmp, 0x02, 0xa1, 0x00, 0x00, 0x00, 0x01, (byte)((1 + channels) >> 8), (byte)(1 + channels), 0x00]);
    }

    [Fact]
    public async Task E131_SmallFrameIsOneUniverse()
    {
        var channel = new RecordingChannel();
        using var driver = new E131Driver(new E131Options("10.0.0.2", Cid: s_cid, SourceName: "Test"), Contexts.With(new FakeHttpHandler((_, _) => new()), channel));
        await driver.OpenAsync(2, TestContext.Current.CancellationToken);

        Assert.True(driver.Write(Leds(2)));
        Assert.True(driver.Write(Leds(2)));

        var packets = channel.Packets.ToArray();
        Assert.Equal(Concat(E131Header(6, 1, 1), Leds(2)), packets[0]);
        Assert.Equal(2, packets[1][111]); // the sequence counts frames
        Assert.Equal("192.0.2.10:5568", channel.Remote!.ToString());
    }

    [Theory]
    [InlineData(false, new[] { 512, 88 })]
    [InlineData(true, new[] { 510, 90 })]
    public void E131_LargeFramesUseConsecutiveUniverses(bool disableSplitting, int[] sizes)
    {
        var rgb = Leds(200);
        var packet = new byte[E131Protocol.MaxPacketSize];
        var name = E131Protocol.SourceName("Test");
        var cid = s_cid.ToByteArray(bigEndian: true);
        var offset = 0;

        Assert.Equal(2, E131Protocol.PacketCount(600, disableSplitting));
        for (var i = 0; i < 2; i++)
        {
            var length = E131Protocol.WritePacket(rgb, i, 7, 9, disableSplitting, cid, name, packet);
            Assert.Equal(Concat(E131Header(sizes[i], 9, 7 + i), rgb[offset..(offset + sizes[i])]), packet[..length]);
            offset += sizes[i];
        }
    }

    [Fact]
    public void E131_RejectsAnInvalidCid()
    {
        Assert.Throws<ArgumentException>(() => E131Options.FromConfig(Ctx.Json("""{ "host": "h", "cid": "not-a-uuid" }""")));
        Assert.Equal(s_cid, E131Options.FromConfig(Ctx.Json("""{ "host": "h", "cid": "{00112233-4455-6677-8899-aabbccddeeff}" }""")).Cid);
        Assert.Equal(new E131Options("h", 5568, 1, false, null, null), E131Options.FromConfig(Ctx.Json("""{ "host": "h" }""")));
    }

    private static byte[] ArtNetHeader(byte sequence, int universe, int length) =>
        [.. "Art-Net\0"u8.ToArray(), 0x00, 0x50, 0x00, 0x0e, sequence, 0, (byte)(universe & 0xff), (byte)((universe >> 8) & 0x7f), (byte)(length >> 8), (byte)length];

    [Fact]
    public async Task ArtNet_RgbFrameIsPaddedToAnEvenLength()
    {
        var channel = new RecordingChannel();
        using var driver = new ArtNetDriver(new ArtNetOptions("10.0.0.3", Universe: 0x123), Contexts.With(new FakeHttpHandler((_, _) => new()), channel));
        await driver.OpenAsync(3, TestContext.Current.CancellationToken);

        driver.Write(Leds(3));

        // 9 channels -> length 10; the datagram carries all 10 (the C++ code sent 9 while announcing 10)
        Assert.Equal(Concat(ArtNetHeader(2, 0x123, 10), Leds(3), [0]), Assert.Single(channel.Packets));
        Assert.Equal("192.0.2.10:6454", channel.Remote!.ToString());
    }

    [Fact]
    public async Task ArtNet_ChannelsPerFixtureLeaveGapsAndSplitAcrossUniverses()
    {
        var channel = new RecordingChannel();
        using var driver = new ArtNetDriver(new ArtNetOptions("h", ChannelsPerFixture: 5, DisableSplitting: true), Contexts.With(new FakeHttpHandler((_, _) => new()), channel));
        await driver.OpenAsync(110, TestContext.Current.CancellationToken);
        var rgb = Leds(110);

        driver.Write(rgb);

        var packets = channel.Packets.ToArray();
        Assert.Equal(2, packets.Length);
        // 102 fixtures of 5 channels fit (510), the 103rd would split
        Assert.Equal(ArtNetHeader(2, 1, 510), packets[0][..18]);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 4, 5, 6, 0, 0 }, packets[0][18..28]);
        Assert.Equal(ArtNetHeader(2, 2, 40), packets[1][..18]);
        Assert.Equal(rgb[(102 * 3)..(103 * 3)], packets[1][18..21]);
    }

    [Fact]
    public void ArtNet_SequenceSkipsZero()
    {
        Assert.Equal(2, ArtNetProtocol.NextSequence(1));
        Assert.Equal(1, ArtNetProtocol.NextSequence(255));
    }

    [Fact]
    public async Task ArtNet_InfiniteRgbwSendsFourChannelsPerLed()
    {
        var channel = new RecordingChannel();
        var rgbw = new InfiniteRgbwOptions(true, 0, 1, Vector3.One);
        using var driver = new ArtNetDriver(new ArtNetOptions("h", Rgbw: rgbw), Contexts.With(new FakeHttpHandler((_, _) => new()), channel));
        await driver.OpenAsync(2, TestContext.Current.CancellationToken);

        Assert.True(driver.WritesFloatColors);
        driver.WriteFloat([new Vector3(1, 0.5f, 0.5f), new Vector3(0, 0, 1)], TimeSpan.Zero);

        var expected = new byte[8];
        new InfiniteRgbwEncoder().Render([new Vector3(1, 0.5f, 0.5f), new Vector3(0, 0, 1)], 0, rgbw, ColorOrder.Rgb, expected);
        Assert.Equal(Concat(ArtNetHeader(2, 1, 8), expected), Assert.Single(channel.Packets));
        Assert.Equal(new byte[] { 191, 64, 64, 64, 0, 0, 255, 0 }, expected); // white = 255 * 0.5 (mixer) / 1, subtracted from RGB
    }

    [Fact]
    public async Task Ddp_ClassicRgbwSends360LedsPerPacket()
    {
        var channel = new RecordingChannel();
        var options = DdpOptions.FromConfig(Ctx.Json("""{ "host": "h", "rgbw": true, "white_algorithm": "subtract_minimum" }"""));
        using var driver = new DdpDriver(options, Contexts.With(new FakeHttpHandler((_, _) => new()), channel));
        await driver.OpenAsync(400, TestContext.Current.CancellationToken);
        var rgb = new byte[400 * 3];
        rgb[0] = 200;
        rgb[1] = 100;
        rgb[2] = 50;

        driver.Write(rgb);

        var packets = channel.Packets.ToArray();
        Assert.Equal(2, packets.Length);
        Assert.Equal(new byte[] { 0x40, 1, 0x1B, 1, 0, 0, 0, 0, 0x05, 0xA0 }, packets[0][..10]);
        Assert.Equal(new byte[] { 150, 50, 0, 50 }, packets[0][10..14]);
        Assert.Equal(new byte[] { 0x41, 1, 0x1B, 1, 0, 0, 0x05, 0xA0, 0, 160 }, packets[1][..10]);
    }

    [Fact]
    public async Task Ddp_InfiniteRgbwUsesTheEncodersColorOrder()
    {
        var channel = new RecordingChannel();
        var rgbw = new InfiniteRgbwOptions(true, 0, 1, Vector3.One);
        using var driver = new DdpDriver(new DdpOptions("h", ColorOrder: ColorOrder.Grb, Rgbw: rgbw), Contexts.With(new FakeHttpHandler((_, _) => new()), channel));
        await driver.OpenAsync(1, TestContext.Current.CancellationToken);

        driver.WriteFloat([new Vector3(1, 0, 0)], TimeSpan.FromMilliseconds(20));

        Assert.Equal(new byte[] { 0x41, 1, 0x1B, 1, 0, 0, 0, 0, 0, 4, 0, 255, 0, 0 }, Assert.Single(channel.Packets));
    }

    [Fact]
    public async Task Udp_DriversDoNotAllocatePerFrame()
    {
        var context = Contexts.With(new FakeHttpHandler((_, _) => new()), new NullChannel());
        using var e131 = new E131Driver(new E131Options("h"), context);
        using var artnet = new ArtNetDriver(new ArtNetOptions("h"), context);
        using var ddp = new DdpDriver(new DdpOptions("h", Rgbw: new InfiniteRgbwOptions(true, 0, 1, Vector3.One)), context);
        foreach (var d in new ILedDriver[] { e131, artnet, ddp })
        {
            await d.OpenAsync(500, TestContext.Current.CancellationToken);
        }

        var rgb = Leds(500);
        var floats = new Vector3[500];
        e131.Write(rgb);
        artnet.Write(rgb);
        ddp.WriteFloat(floats, TimeSpan.Zero);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++)
        {
            e131.Write(rgb);
            artnet.Write(rgb);
            ddp.WriteFloat(floats, TimeSpan.Zero);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class NullChannel : Transport.IDatagramChannel
    {
        public void Connect(System.Net.IPEndPoint remote)
        {
        }

        public bool Send(ReadOnlySpan<byte> datagram) => true;

        public void Dispose()
        {
        }
    }
}
