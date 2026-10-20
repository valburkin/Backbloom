using System.Buffers.Binary;

namespace Overglow.Devices.Drivers.Ddp;

/// <summary>
/// Distributed Display Protocol packets as built by <c>DriverNetDDP::writeFiniteColors</c>
/// (sources/led-drivers/net/DriverNetDDP.cpp): a 10-byte header (flags, sequence, data type 0x0B for RGB or 0x1B for
/// RGBW, destination 1, big-endian byte offset and length) followed by at most 480 RGB or 360 RGBW LEDs (1440 bytes).
/// The last packet of a frame has the push flag set.
/// </summary>
public static class DdpProtocol
{
    public const int DefaultPort = 4048;
    public const int HeaderSize = 10;
    public const int MaxLedsPerPacket = 480;
    public const int MaxRgbwLedsPerPacket = 360;
    public const int MaxDataPerPacket = MaxLedsPerPacket * 3;
    public const byte FlagsVersion1 = 0x40;
    public const byte FlagPush = 0x01;
    public const byte DataTypeRgb = 0x0B;
    public const byte DataTypeRgbw = 0x1B;
    public const byte DestinationDefault = 0x01;

    public const int MaxPacketSize = HeaderSize + MaxDataPerPacket;

    public static int PacketCount(int ledCount) => ledCount == 0 ? 0 : (ledCount + MaxLedsPerPacket - 1) / MaxLedsPerPacket;

    /// <summary>Packets needed for <paramref name="dataLength"/> bytes of color data.</summary>
    public static int PacketCountForBytes(int dataLength) => (dataLength + MaxDataPerPacket - 1) / MaxDataPerPacket;

    /// <summary>The sequence number following <paramref name="previous"/>: cycles 1..15 (0 means "not used" in DDP).</summary>
    public static byte NextSequence(byte previous) => (byte)((previous % 0x0F) + 1);

    /// <summary>Writes packet <paramref name="index"/> of an RGB frame and returns its length.</summary>
    public static int WriteFrame(ReadOnlySpan<byte> rgb, int index, byte sequence, Span<byte> packet) =>
        WriteFrame(rgb, index, sequence, DataTypeRgb, packet);

    /// <summary>Writes packet <paramref name="index"/> of a frame (RGB or RGBW data) and returns its length.</summary>
    public static int WriteFrame(ReadOnlySpan<byte> data, int index, byte sequence, byte dataType, Span<byte> packet)
    {
        var offset = index * MaxDataPerPacket;
        var size = Math.Min(data.Length - offset, MaxDataPerPacket);
        var isLast = offset + size >= data.Length;
        packet[0] = isLast ? (byte)(FlagsVersion1 | FlagPush) : FlagsVersion1;
        packet[1] = sequence;
        packet[2] = dataType;
        packet[3] = DestinationDefault;
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], (uint)offset);
        BinaryPrimitives.WriteUInt16BigEndian(packet[8..], (ushort)size);
        data.Slice(offset, size).CopyTo(packet[HeaderSize..]);
        return HeaderSize + size;
    }
}
