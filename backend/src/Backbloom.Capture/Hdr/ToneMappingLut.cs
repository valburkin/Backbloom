namespace Backbloom.Capture.Hdr;

/// <summary>
/// The HDR to SDR lookup table of the screen capture: 256 x 256 x 256 RGB triplets indexed by the captured RGB value
/// (red changes fastest). Ported from LutLoader (LUT_INDEX, TABLE_SIZE). Immutable once built.
/// </summary>
public sealed class ToneMappingLut
{
    /// <summary>Bytes of one table: 256^3 RGB triplets.</summary>
    public const int TableSize = 256 * 256 * 256 * 3;

    private readonly byte[] _table;

    /// <summary>Takes ownership of <paramref name="table"/> (at least <see cref="TableSize"/> bytes).</summary>
    public ToneMappingLut(byte[] table, string source)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfLessThan(table.Length, TableSize);
        _table = table;
        Source = source;
    }

    /// <summary>Where the table came from (file path or "built-in default").</summary>
    public string Source { get; }

    public ReadOnlySpan<byte> Table => _table.AsSpan(0, TableSize);

    /// <summary>Byte offset of the triplet for an RGB value.</summary>
    public static int Index(int r, int g, int b) => (r + (g << 8) + (b << 16)) * 3;

    /// <summary>Tone-mapped RGB of one RGB value.</summary>
    public (byte R, byte G, byte B) Map(byte r, byte g, byte b)
    {
        var i = Index(r, g, b);
        return (_table[i], _table[i + 1], _table[i + 2]);
    }
}
