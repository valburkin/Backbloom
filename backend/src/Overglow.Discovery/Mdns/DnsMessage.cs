using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Overglow.Discovery.Mdns;

public enum DnsType : ushort
{
    A = 1,
    NamePointer = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Any = 255,
}

/// <summary>One question of a DNS message.</summary>
/// <param name="UnicastResponse">mDNS "QU" bit: the querier asks for a unicast answer.</param>
public sealed record DnsQuestion(string Name, DnsType Type, bool UnicastResponse = false);

/// <summary>One resource record. <see cref="Data"/> is a <see cref="string"/> (PTR target), <see cref="SrvData"/>,
/// <see cref="IPAddress"/> (A/AAAA), a list of strings (TXT) or the raw bytes of other types.</summary>
/// <param name="CacheFlush">mDNS cache-flush bit of the class field.</param>
public sealed record DnsRecord(string Name, DnsType Type, uint Ttl, object Data, bool CacheFlush = false);

public sealed record SrvData(ushort Priority, ushort Weight, ushort Port, string Target);

/// <summary>
/// A DNS message as used by multicast DNS (RFC 6762/6763): header, questions, answers and additional records, with
/// name compression on reading. Written without compression (mDNS messages here are small).
/// </summary>
public sealed class DnsMessage
{
    private const ushort ClassIn = 1;
    private const ushort TopBit = 0x8000;

    public ushort Id { get; init; }

    public bool IsResponse { get; init; }

    public IReadOnlyList<DnsQuestion> Questions { get; init; } = [];

    public IReadOnlyList<DnsRecord> Answers { get; init; } = [];

    public IReadOnlyList<DnsRecord> Additionals { get; init; } = [];

    /// <summary>Answers, authority and additional records together.</summary>
    public IEnumerable<DnsRecord> AllRecords => Answers.Concat(Additionals);

    public static DnsMessage Query(params DnsQuestion[] questions) => new() { Questions = questions };

    public byte[] Encode()
    {
        using var stream = new MemoryStream(512);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, Id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], IsResponse ? (ushort)0x8400 : (ushort)0); // QR + AA for responses
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)Questions.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)Answers.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)Additionals.Count);
        stream.Write(header);
        foreach (var q in Questions)
        {
            WriteName(stream, q.Name);
            WriteUInt16(stream, (ushort)q.Type);
            WriteUInt16(stream, (ushort)(ClassIn | (q.UnicastResponse ? TopBit : 0)));
        }

        foreach (var r in Answers.Concat(Additionals))
        {
            WriteRecord(stream, r);
        }

        return stream.ToArray();
    }

    /// <summary>Parses a message; throws <see cref="InvalidDataException"/> when it is malformed.</summary>
    public static DnsMessage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            throw new InvalidDataException("DNS message shorter than its header.");
        }

        var reader = new Reader(data.ToArray());
        var id = reader.UInt16();
        var flags = reader.UInt16();
        var qd = reader.UInt16();
        var an = reader.UInt16();
        var ns = reader.UInt16();
        var ar = reader.UInt16();
        var questions = new List<DnsQuestion>(qd);
        for (var i = 0; i < qd; i++)
        {
            var name = reader.Name();
            var type = (DnsType)reader.UInt16();
            var cls = reader.UInt16();
            questions.Add(new DnsQuestion(name, type, (cls & TopBit) != 0));
        }

        var answers = ReadRecords(ref reader, an);
        ReadRecords(ref reader, ns);
        var additionals = ReadRecords(ref reader, ar);
        return new DnsMessage { Id = id, IsResponse = (flags & 0x8000) != 0, Questions = questions, Answers = answers, Additionals = additionals };
    }

    /// <summary>Compares DNS names case-insensitively, ignoring a trailing dot.</summary>
    public static bool NameEquals(string a, string b) =>
        string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static List<DnsRecord> ReadRecords(ref Reader reader, int count)
    {
        var records = new List<DnsRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var name = reader.Name();
            var type = (DnsType)reader.UInt16();
            var cls = reader.UInt16();
            var ttl = reader.UInt32();
            var length = reader.UInt16();
            var end = reader.Position + length;
            if (end > reader.Length)
            {
                throw new InvalidDataException("DNS record runs past the end of the message.");
            }

            object value = type switch
            {
                DnsType.NamePointer => reader.Name(),
                DnsType.Srv => new SrvData(reader.UInt16(), reader.UInt16(), reader.UInt16(), reader.Name()),
                DnsType.A when length == 4 => new IPAddress(reader.Bytes(4)),
                DnsType.Aaaa when length == 16 => new IPAddress(reader.Bytes(16)),
                DnsType.Txt => ReadTxt(ref reader, end),
                _ => reader.Bytes(length),
            };
            reader.Position = end;
            records.Add(new DnsRecord(name, type, ttl, value, (cls & TopBit) != 0));
        }

        return records;
    }

    private static List<string> ReadTxt(ref Reader reader, int end)
    {
        var strings = new List<string>();
        while (reader.Position < end)
        {
            var length = reader.Byte();
            strings.Add(Encoding.UTF8.GetString(reader.Bytes(Math.Min(length, end - reader.Position))));
        }

        return strings;
    }

    private static void WriteRecord(MemoryStream stream, DnsRecord r)
    {
        WriteName(stream, r.Name);
        WriteUInt16(stream, (ushort)r.Type);
        WriteUInt16(stream, (ushort)(ClassIn | (r.CacheFlush ? TopBit : 0)));
        Span<byte> ttl = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(ttl, r.Ttl);
        stream.Write(ttl);

        using var data = new MemoryStream();
        switch (r.Data)
        {
            case string target:
                WriteName(data, target);
                break;
            case SrvData srv:
                WriteUInt16(data, srv.Priority);
                WriteUInt16(data, srv.Weight);
                WriteUInt16(data, srv.Port);
                WriteName(data, srv.Target);
                break;
            case IPAddress address:
                data.Write(address.GetAddressBytes());
                break;
            case IEnumerable<string> txt:
                foreach (var s in txt)
                {
                    var bytes = Encoding.UTF8.GetBytes(s);
                    data.WriteByte((byte)Math.Min(bytes.Length, 255));
                    data.Write(bytes.AsSpan(0, Math.Min(bytes.Length, 255)));
                }

                break;
            case byte[] raw:
                data.Write(raw);
                break;
            default:
                throw new ArgumentException($"Unsupported DNS record data {r.Data.GetType().Name}.", nameof(r));
        }

        WriteUInt16(stream, (ushort)data.Length);
        data.WriteTo(stream);
    }

    private static void WriteName(MemoryStream stream, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63)
            {
                throw new ArgumentException($"DNS label '{label}' is longer than 63 bytes.", nameof(name));
            }

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
    }

    private static void WriteUInt16(MemoryStream stream, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        stream.Write(b);
    }

    private struct Reader(byte[] data)
    {
        private readonly byte[] _data = data;

        public int Position { get; set; }

        public readonly int Length => _data.Length;

        public byte Byte()
        {
            Require(1);
            return _data[Position++];
        }

        public ushort UInt16()
        {
            Require(2);
            var v = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Position));
            Position += 2;
            return v;
        }

        public uint UInt32()
        {
            Require(4);
            var v = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(Position));
            Position += 4;
            return v;
        }

        public byte[] Bytes(int count)
        {
            Require(count);
            var v = _data.AsSpan(Position, count).ToArray();
            Position += count;
            return v;
        }

        /// <summary>Reads a possibly compressed name; the position ends after its first occurrence.</summary>
        public string Name()
        {
            var labels = new List<string>();
            var position = Position;
            var jumped = false;
            for (var hops = 0; hops < 64; hops++)
            {
                if (position >= _data.Length)
                {
                    throw new InvalidDataException("DNS name runs past the end of the message.");
                }

                var length = _data[position];
                if (length == 0)
                {
                    if (!jumped)
                    {
                        Position = position + 1;
                    }

                    return string.Join('.', labels) + ".";
                }

                if ((length & 0xC0) == 0xC0)
                {
                    if (position + 1 >= _data.Length)
                    {
                        throw new InvalidDataException("Truncated DNS name pointer.");
                    }

                    if (!jumped)
                    {
                        Position = position + 2;
                    }

                    jumped = true;
                    position = ((length & 0x3F) << 8) | _data[position + 1];
                    continue;
                }

                if (position + 1 + length > _data.Length)
                {
                    throw new InvalidDataException("DNS label runs past the end of the message.");
                }

                labels.Add(Encoding.UTF8.GetString(_data, position + 1, length));
                position += 1 + length;
            }

            throw new InvalidDataException("Too many DNS name compression pointers.");
        }

        private readonly void Require(int count)
        {
            if (Position + count > _data.Length)
            {
                throw new InvalidDataException("DNS message is truncated.");
            }
        }
    }
}
