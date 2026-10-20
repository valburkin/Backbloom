using System.Buffers.Binary;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Backbloom.Platform.Windows.Desktop;

/// <summary>
/// The Backbloom icon (branding/backbloom.ico, embedded) as Win32 icons of a requested size. The .ico directory is read
/// here and the best image handed to <c>CreateIconFromResourceEx</c>, which takes PNG and bitmap images alike.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static unsafe class AppIcon
{
    private static readonly Lazy<byte[]> s_file = new(() =>
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Backbloom.Platform.Windows.backbloom.ico")
            ?? throw new InvalidOperationException("The Backbloom icon resource is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    /// <summary>Creates an icon of <paramref name="size"/> pixels; the caller destroys it with <c>DestroyIcon</c>.</summary>
    public static HICON Create(int size)
    {
        var file = s_file.Value;
        var (offset, length) = SelectImage(file, size);
        fixed (byte* bits = &file[offset])
        {
            return CreateIconFromResourceEx(bits, (uint)length, TRUE, 0x00030000, size, size, 0);
        }
    }

    /// <summary>The image whose width is the smallest one at least <paramref name="size"/> (else the largest).</summary>
    internal static (int Offset, int Length) SelectImage(ReadOnlySpan<byte> ico, int size)
    {
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico[2..]) != 1)
        {
            throw new InvalidDataException("Not an icon file.");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico[4..]);
        (int Width, int Offset, int Length)? best = null;
        for (var i = 0; i < count; i++)
        {
            var entry = ico.Slice(6 + (i * 16), 16);
            var width = entry[0] == 0 ? 256 : entry[0];
            var candidate = (width, (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]));
            var better = best is not { } b
                || (width >= size && (b.Width < size || width < b.Width))
                || (width < size && b.Width < size && width > b.Width);
            if (better)
            {
                best = candidate;
            }
        }

        return best is { } chosen && chosen.Offset + chosen.Length <= ico.Length
            ? (chosen.Offset, chosen.Length)
            : throw new InvalidDataException("The icon file has no usable image.");
    }
}
