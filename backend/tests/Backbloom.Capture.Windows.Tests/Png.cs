using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Backbloom.Core.Imaging;

namespace Backbloom.Capture.Windows.Tests;

/// <summary>Minimal PNG writer (RGB, 8 bit) for looking at captured frames.</summary>
internal static class Png
{
    private static readonly uint[] s_crcTable = BuildCrcTable();

    public static void Write(string path, Frame frame)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + (frame.Width * 3)];
            for (var y = 0; y < frame.Height; y++)
            {
                var src = frame.ReadOnlyPixels.Slice(y * frame.Stride, frame.Stride);
                for (var x = 0; x < frame.Width; x++)
                {
                    row[1 + (x * 3)] = src[(x * 4) + 2];
                    row[2 + (x * 3)] = src[(x * 4) + 1];
                    row[3 + (x * 3)] = src[x * 4];
                }

                z.Write(row);
            }
        }

        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), frame.Height);
        header[8] = 8;
        header[9] = 2;
        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", raw.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        stream.Write(buffer);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        stream.Write(buffer);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data)
        {
            crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
