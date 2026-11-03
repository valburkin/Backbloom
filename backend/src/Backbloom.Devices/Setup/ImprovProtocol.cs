using System.Text;

namespace Backbloom.Devices.Setup;

/// <summary>Improv serial packet types (https://www.improv-wifi.com/serial/).</summary>
public enum ImprovPacketType : byte
{
    CurrentState = 0x01,
    ErrorState = 0x02,
    RpcCommand = 0x03,
    RpcResult = 0x04,
}

/// <summary>The device's provisioning state (a <see cref="ImprovPacketType.CurrentState"/> packet).</summary>
public enum ImprovState : byte
{
    /// <summary>Provisioning isn't available (e.g. Wi-Fi is off).</summary>
    Stopped = 0x00,

    /// <summary>Improv over Bluetooth only: the user has to press the device's button first; kept for devices that send it.</summary>
    AuthorizationRequired = 0x01,

    /// <summary>Ready for Wi-Fi credentials.</summary>
    Ready = 0x02,

    /// <summary>Connecting to the network it was given.</summary>
    Provisioning = 0x03,

    /// <summary>On the network.</summary>
    Provisioned = 0x04,
}

/// <summary>Error codes (an <see cref="ImprovPacketType.ErrorState"/> packet).</summary>
public enum ImprovError : byte
{
    None = 0x00,
    InvalidRpcPacket = 0x01,
    UnknownRpcCommand = 0x02,
    UnableToConnect = 0x03,
    NotAuthorized = 0x04,
    BadHostname = 0x05,
    Unknown = 0xFF,
}

/// <summary>RPC commands.</summary>
public enum ImprovCommand : byte
{
    SendWifiSettings = 0x01,
    RequestCurrentState = 0x02,
    RequestDeviceInfo = 0x03,
    RequestScannedNetworks = 0x04,
}

/// <summary>One received packet: its type and data (without header, length and checksum).</summary>
public sealed record ImprovPacket(ImprovPacketType Type, byte[] Data)
{
    /// <summary>For an RPC result: the command it answers and its strings (empty when malformed).</summary>
    public (ImprovCommand Command, IReadOnlyList<string> Strings) ReadResult()
    {
        if (Type != ImprovPacketType.RpcResult || Data.Length < 2)
        {
            return (0, []);
        }

        var strings = new List<string>();
        var end = Math.Min(Data.Length, 2 + Data[1]);
        var i = 2;
        while (i < end)
        {
            var length = Data[i++];
            if (i + length > end)
            {
                break;
            }

            strings.Add(Encoding.UTF8.GetString(Data, i, length));
            i += length;
        }

        return ((ImprovCommand)Data[0], strings);
    }
}

/// <summary>
/// The Improv Wi-Fi serial protocol, version 1: <c>"IMPROV"</c>, version, type, data length, data, checksum (the low
/// byte of the sum of every byte before it), then a newline (as the reference SDK sends; boards ignore it, and the
/// parser skips anything between packets). RPC commands carry the command, the length of its data and the data;
/// RPC results the command they answer, the total length and length-prefixed strings.
/// </summary>
public static class ImprovProtocol
{
    public const byte Version = 1;

    /// <summary>The longest data a packet can carry (one length byte).</summary>
    public const int MaxData = 255;

    private const int HeaderLength = 6;

    // header (6), version, type, length
    private const int PrefixLength = HeaderLength + 3;

    public static ReadOnlySpan<byte> Header => "IMPROV"u8;

    /// <summary>An RPC command packet with the newline the reference client appends.</summary>
    public static byte[] Command(ImprovCommand command, ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > MaxData - 2)
        {
            throw new ArgumentException("The Improv command data is too long.", nameof(payload));
        }

        var data = new byte[2 + payload.Length];
        data[0] = (byte)command;
        data[1] = (byte)payload.Length;
        payload.CopyTo(data.AsSpan(2));
        return Packet(ImprovPacketType.RpcCommand, data);
    }

    /// <summary>A packet of any type (the fake boards in the tests answer with it).</summary>
    public static byte[] Packet(ImprovPacketType type, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxData)
        {
            throw new ArgumentException("The Improv packet data is too long.", nameof(data));
        }

        var packet = new byte[PrefixLength + data.Length + 2];
        Header.CopyTo(packet);
        packet[6] = Version;
        packet[7] = (byte)type;
        packet[8] = (byte)data.Length;
        data.CopyTo(packet.AsSpan(PrefixLength));
        packet[^2] = Checksum(packet.AsSpan(0, packet.Length - 2));
        packet[^1] = (byte)'\n';
        return packet;
    }

    /// <summary>The "send Wi-Fi settings" data: SSID and password, each with its length.</summary>
    public static byte[] WifiSettings(string ssid, string password)
    {
        ArgumentNullException.ThrowIfNull(ssid);
        ArgumentNullException.ThrowIfNull(password);
        var s = Encoding.UTF8.GetBytes(ssid);
        var p = Encoding.UTF8.GetBytes(password);
        if (s.Length > 255 || p.Length > 255 || s.Length + p.Length + 2 > MaxData - 2)
        {
            throw new ArgumentException("The network name or password is too long.");
        }

        var data = new byte[2 + s.Length + p.Length];
        data[0] = (byte)s.Length;
        s.CopyTo(data, 1);
        data[1 + s.Length] = (byte)p.Length;
        p.CopyTo(data, 2 + s.Length);
        return data;
    }

    /// <summary>An RPC result with strings (for the tests' fake boards).</summary>
    public static byte[] Result(ImprovCommand command, params string[] strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var body = new List<byte>();
        foreach (var s in strings)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            body.Add((byte)bytes.Length);
            body.AddRange(bytes);
        }

        return Packet(ImprovPacketType.RpcResult, [(byte)command, (byte)body.Count, .. body]);
    }

    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes)
        {
            sum += b;
        }

        return (byte)sum;
    }

    /// <summary>
    /// Takes the first complete, valid packet out of <paramref name="buffer"/> (the bytes received so far): bytes
    /// before a header (boot messages, log lines, newlines) and packets with a wrong checksum are dropped. Null when no
    /// complete packet is there yet; the incomplete rest stays in the buffer.
    /// </summary>
    public static ImprovPacket? TryRead(List<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        while (true)
        {
            var start = IndexOfHeader(buffer);
            if (start < 0)
            {
                // (a header cut off at the end counts as found)
                buffer.Clear();
                return null;
            }

            buffer.RemoveRange(0, start);
            if (buffer.Count < PrefixLength)
            {
                return null;
            }

            var length = buffer[8];
            var total = PrefixLength + length + 1;
            if (buffer.Count < total)
            {
                return null;
            }

            var packet = buffer.GetRange(0, total).ToArray();
            if (packet[6] != Version || Checksum(packet.AsSpan(0, total - 1)) != packet[^1])
            {
                // not a packet after all (or damaged): look for the next header
                buffer.RemoveAt(0);
                continue;
            }

            buffer.RemoveRange(0, total);
            return new ImprovPacket((ImprovPacketType)packet[7], packet.AsSpan(PrefixLength, length).ToArray());
        }
    }

    private static int IndexOfHeader(List<byte> buffer)
    {
        var header = Header;
        for (var i = 0; i + HeaderLength <= buffer.Count; i++)
        {
            var match = true;
            for (var j = 0; j < HeaderLength && match; j++)
            {
                match = buffer[i + j] == header[j];
            }

            if (match)
            {
                return i;
            }
        }

        // a header cut off at the end of what was received
        for (var i = Math.Max(0, buffer.Count - HeaderLength + 1); i < buffer.Count; i++)
        {
            var match = true;
            for (var j = 0; i + j < buffer.Count && match; j++)
            {
                match = buffer[i + j] == header[j];
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
