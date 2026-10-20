namespace Backbloom.Core.Borders;

/// <summary>
/// A detected black border: rows at the top and bottom (<see cref="HorizontalSize"/>) and columns at the left and
/// right (<see cref="VerticalSize"/>), or <see cref="IsUnknown"/> when the picture is too dark to tell. Port of
/// <c>backbloom::BlackBorder</c> (sources/blackborder/BlackBorderDetector.cpp), including its equality: all unknown
/// borders are equal whatever their sizes.
/// </summary>
public readonly struct BlackBorder(bool isUnknown, int horizontalSize, int verticalSize) : IEquatable<BlackBorder>
{
    /// <summary>The state before anything was detected (C++: <c>{ true, -1, -1 }</c>).</summary>
    public static BlackBorder Unknown => new(true, -1, -1);

    /// <summary>No border.</summary>
    public static BlackBorder None => new(false, 0, 0);

    public bool IsUnknown { get; } = isUnknown;

    /// <summary>Black rows at the top and at the bottom.</summary>
    public int HorizontalSize { get; } = horizontalSize;

    /// <summary>Black columns at the left and at the right.</summary>
    public int VerticalSize { get; } = verticalSize;

    public static bool operator ==(BlackBorder left, BlackBorder right) => left.Equals(right);

    public static bool operator !=(BlackBorder left, BlackBorder right) => !left.Equals(right);

    public bool Equals(BlackBorder other) =>
        IsUnknown ? other.IsUnknown : !other.IsUnknown && HorizontalSize == other.HorizontalSize && VerticalSize == other.VerticalSize;

    public override bool Equals(object? obj) => obj is BlackBorder other && Equals(other);

    public override int GetHashCode() => IsUnknown ? 1 : HashCode.Combine(HorizontalSize, VerticalSize);

    public override string ToString() => IsUnknown ? "unknown" : $"{HorizontalSize} rows, {VerticalSize} columns";
}
