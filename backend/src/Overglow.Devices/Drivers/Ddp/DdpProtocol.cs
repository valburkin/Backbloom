using System.Buffers.Binary;

namespace Overglow.Devices.Drivers.Ddp;

/// <summary>
/// Distributed Display Protocol packets as built by <c>DriverNetDDP::writeFiniteColors</c>
/// (sources/led-drivers/net/DriverNetDDP.cpp), RGB variant: a 10-byte header (flags, sequence, data type 0x0B,
/// destination 1, big-endian byte offset and length) followed by at most 480 LEDs. The last packet of a frame has the
/// push flag set.
/// </summary>
public static class DdpProtocol
{
    public const int DefaultPort = 4048;
    public const int HeaderSize = 10;
    public const int MaxLedsPerPacket = 480;
    public const byte FlagsVersion1 = 0x40;
    public const byte FlagPush = 0x01;
    public const byte DataTypeRgb = 0x0B;
    public const byte DestinationDefault = 0x01;

    public const int MaxPacketSize = HeaderSize + (MaxLedsPerPacket * 3);

    public static int PacketCount(int ledCount) => ledCount == 0 ? 0 : (ledCount + MaxLedsPerPacket - 1) / MaxLedsPerPacket;

    /// <summary>The sequence number following <paramref name="previous"/>: cycles 1..15 (0 means "not used" in DDP).</summary>
    public static byte NextSequence(byte previous) => (byte)((previous % 0x0F) + 1);

    /// <summary>Writes packet <paramref name="index"/> of a frame and returns its length.</summary>
    public static int WriteFrame(ReadOnlySpan<byte> rgb, int index, byte sequence, Span<byte> packet)
    {
        var maxBytes = MaxLedsPerPacket * 3;
        var offset = index * maxBytes;
        var size = Math.Min(rgb.Length - offset, maxBytes);
        var isLast = offset + size >= rgb.Length;
        packet[0] = isLast ? (byte)(FlagsVersion1 | FlagPush) : FlagsVersion1;
        packet[1] = sequence;
        packet[2] = DataTypeRgb;
        packet[3] = DestinationDefault;
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], (uint)offset);
        BinaryPrimitives.WriteUInt16BigEndian(packet[8..], (ushort)size);
        rgb.Slice(offset, size).CopyTo(packet[HeaderSize..]);
        return HeaderSize + size;
    }
}
