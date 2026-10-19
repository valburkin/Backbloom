using Overglow.Devices.Drivers.Ddp;

namespace Overglow.Devices.Tests;

public sealed class DdpTests
{
    [Fact]
    public void SmallFrame_IsOnePushPacket()
    {
        var rgb = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var packet = new byte[DdpProtocol.MaxPacketSize];

        var length = DdpProtocol.WriteFrame(rgb, 0, 5, packet);

        Assert.Equal(1, DdpProtocol.PacketCount(3));
        Assert.Equal(new byte[] { 0x41, 5, 0x0B, 0x01, 0, 0, 0, 0, 0, 9, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, packet[..length]);
    }

    [Fact]
    public void LargeFrame_IsSplitInto480LedPacketsAndOnlyTheLastPushes()
    {
        var rgb = Enumerable.Range(0, 481 * 3).Select(i => (byte)i).ToArray();
        var packet = new byte[DdpProtocol.MaxPacketSize];

        Assert.Equal(2, DdpProtocol.PacketCount(481));

        var l0 = DdpProtocol.WriteFrame(rgb, 0, 1, packet);
        Assert.Equal(10 + 1440, l0);
        Assert.Equal(new byte[] { 0x40, 1, 0x0B, 0x01, 0, 0, 0, 0, 0x05, 0xA0 }, packet[..10]);
        Assert.Equal(rgb[..1440], packet[10..l0]);

        var l1 = DdpProtocol.WriteFrame(rgb, 1, 1, packet);
        Assert.Equal(new byte[] { 0x41, 1, 0x0B, 0x01, 0, 0, 0x05, 0xA0, 0, 3 }, packet[..10]);
        Assert.Equal(rgb[1440..], packet[10..l1]);
    }

    [Fact]
    public void Sequence_Cycles1To15()
    {
        byte s = 0;
        var seen = new List<byte>();
        for (var i = 0; i < 16; i++)
        {
            s = DdpProtocol.NextSequence(s);
            seen.Add(s);
        }

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 1], seen);
    }

    [Fact]
    public async Task Driver_SendsToTheConfiguredPortWithOneSequencePerFrame()
    {
        var udp = new RecordingChannel();
        using var driver = new DdpDriver(new DdpOptions("ddp-test.local", 4049), Contexts.With(new FakeHttpHandler((_, _) => new()), udp));

        await driver.OpenAsync(1, TestContext.Current.CancellationToken);
        driver.Write(new byte[] { 9, 8, 7 });
        driver.Write(new byte[] { 6, 5, 4 });

        Assert.Equal("192.0.2.10:4049", udp.Remote!.ToString());
        Assert.Equal(
        [
            new byte[] { 0x41, 1, 0x0B, 1, 0, 0, 0, 0, 0, 3, 9, 8, 7 },
            new byte[] { 0x41, 2, 0x0B, 1, 0, 0, 0, 0, 0, 3, 6, 5, 4 },
        ], udp.Packets);
    }

    [Fact]
    public void Driver_RejectsAnInvalidPort() =>
        Assert.Throws<ArgumentException>(() => new DdpDriver(new DdpOptions("h", 70000), Contexts.With(new FakeHttpHandler((_, _) => new()), new RecordingChannel())));
}
