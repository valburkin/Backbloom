using System.Buffers.Binary;
using System.Text;

namespace Overglow.Devices.Drivers.E131;

/// <summary>
/// E1.31 (sACN) data packets as built by <c>DriverNetUdpE131::prepare</c> / <c>writeFiniteColors</c>
/// (sources/led-drivers/net/DriverNetUdpE131.cpp): root, framing and DMP layers, priority 100, start code 0, then up
/// to 512 channels (510 with "disable splitting", so no LED is split across universes). Consecutive universes carry
/// the rest of a frame; all packets of a frame share one sequence number.
/// </summary>
public static class E131Protocol
{
    public const int DefaultPort = 5568;
    public const int HeaderSize = 126; // E131_DMP_DATA (125) + start code
    public const int MaxPacketSize = HeaderSize + 512;

    private static readonly byte[] s_acnId = [0x41, 0x53, 0x43, 0x2d, 0x45, 0x31, 0x2e, 0x31, 0x37, 0x00, 0x00, 0x00];

    /// <summary>Channels per universe.</summary>
    public static int ChannelsPerUniverse(bool disableSplitting) => disableSplitting ? 510 : 512;

    public static int PacketCount(int channels, bool disableSplitting) =>
        channels == 0 ? 0 : (channels + ChannelsPerUniverse(disableSplitting) - 1) / ChannelsPerUniverse(disableSplitting);

    /// <summary>The source name field: UTF-8, at most 63 bytes and a terminating zero (snprintf into 64 bytes).</summary>
    public static byte[] SourceName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var bytes = Encoding.UTF8.GetBytes(name);
        var field = new byte[64];
        bytes.AsSpan(0, Math.Min(bytes.Length, 63)).CopyTo(field);
        return field;
    }

    /// <summary>Writes packet <paramref name="index"/> of a frame and returns its length.</summary>
    /// <param name="rgb">The frame's channel data (three bytes per LED).</param>
    /// <param name="cid">The component id in RFC 4122 (big-endian) byte order.</param>
    /// <param name="sourceName">64-byte source name field (see <see cref="SourceName"/>).</param>
    public static int WritePacket(
        ReadOnlySpan<byte> rgb,
        int index,
        int firstUniverse,
        byte sequence,
        bool disableSplitting,
        ReadOnlySpan<byte> cid,
        ReadOnlySpan<byte> sourceName,
        Span<byte> packet)
    {
        var max = ChannelsPerUniverse(disableSplitting);
        var start = index * max;
        var count = Math.Min(max, rgb.Length - start);
        packet[..MaxPacketSize].Clear();

        // Root layer
        BinaryPrimitives.WriteUInt16BigEndian(packet, 16);
        s_acnId.CopyTo(packet[4..]);
        BinaryPrimitives.WriteUInt16BigEndian(packet[16..], (ushort)(0x7000 | (110 + count)));
        BinaryPrimitives.WriteUInt32BigEndian(packet[18..], 0x00000004); // VECTOR_ROOT_E131_DATA
        cid[..16].CopyTo(packet[22..]);

        // Framing layer
        BinaryPrimitives.WriteUInt16BigEndian(packet[38..], (ushort)(0x7000 | (88 + count)));
        BinaryPrimitives.WriteUInt32BigEndian(packet[40..], 0x00000002); // VECTOR_E131_DATA_PACKET
        sourceName[..64].CopyTo(packet[44..]);
        packet[108] = 100; // priority
        packet[111] = sequence;
        BinaryPrimitives.WriteUInt16BigEndian(packet[113..], (ushort)(firstUniverse + index));

        // DMP layer
        BinaryPrimitives.WriteUInt16BigEndian(packet[115..], (ushort)(0x7000 | (11 + count)));
        packet[117] = 0x02; // VECTOR_DMP_SET_PROPERTY
        packet[118] = 0xa1;
        BinaryPrimitives.WriteUInt16BigEndian(packet[121..], 1); // address increment
        BinaryPrimitives.WriteUInt16BigEndian(packet[123..], (ushort)(1 + count));
        rgb.Slice(start, count).CopyTo(packet[HeaderSize..]);
        return HeaderSize + count;
    }
}
