using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Backbloom.Core.Color;
using Backbloom.Devices.Rgbw;

namespace Backbloom.Devices.Drivers.File;

/// <summary>Settings of the "file" debug driver (sources/led-drivers/schemas/schema-file.json).</summary>
/// <param name="InfiniteColorEngine">Write the unrounded colors (0..255 with four decimals) instead of bytes.</param>
/// <param name="Rgbw">RGBW by the Infinite Color Engine: write the encoded RGBW bytes.</param>
public sealed record FileDriverOptions(
    string Output,
    bool PrintTimeStamp = true,
    bool InfiniteColorEngine = false,
    InfiniteRgbwOptions? Rgbw = null,
    ColorOrder ColorOrder = ColorOrder.Rgb)
{
    public static FileDriverOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new FileDriverOptions(
            DeviceConfig.GetString(config, "output"),
            DeviceConfig.GetBool(config, "printTimeStamp", true),
            DeviceConfig.GetBool(config, "infiniteColorEngine", false),
            InfiniteRgbwOptions.FromConfig(config),
            ColorOrders.Parse(DeviceConfig.GetString(config, "colorOrder", "rgb")));
    }

    /// <summary>The file actually written: "/dev/null" or an empty setting mean the platform's null device.</summary>
    public string ResolvedPath =>
        string.IsNullOrWhiteSpace(Output) || Output == "/dev/null" ? (OperatingSystem.IsWindows() ? @"\\.\NUL" : "/dev/null") : Output;
}

/// <summary>
/// Writes every frame as a text line, e.g. <c>2026-10-19T20:15:00.123 | +16.7 [{255,0,0}{0,0,0}]</c>. Port of
/// <c>DriverOtherFile</c> (sources/led-drivers/other/DriverOtherFile.cpp), including its deep color output
/// (<c>{ 255.0000,   0.0000,  12.5000}, ...</c>) and the RGBW output of the Infinite Color Engine (<c>{r,g,b,w}, ...</c>).
/// </summary>
public sealed class FileDriver : IFloatColorDriver
{
    public const string TypeName = "file";

    private readonly FileDriverOptions _options;
    private readonly InfiniteRgbwOptions _rgbw;
    private readonly InfiniteRgbwEncoder _encoder = new();
    private readonly Func<DateTime> _clock;
    private char[] _line = [];
    private byte[] _rgbwBuffer = [];
    private StreamWriter? _writer;
    private long _lastWrite;

    public FileDriver(FileDriverOptions options, Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _rgbw = options.Rgbw ?? InfiniteRgbwOptions.Disabled;
        _clock = clock ?? (() => DateTime.Now);
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_5_debug",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, _) => new FileDriver(FileDriverOptions.FromConfig(config)));

    public string Type => TypeName;

    public RetryPolicy RetryPolicy { get; } = new(3, TimeSpan.FromSeconds(1));

    public string? Status => null;

    public bool WritesFloatColors => _options.InfiniteColorEngine || _rgbw.Enabled;

    public ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        var stream = new FileStream(_options.ResolvedPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 64 * 1024);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
        _line = new char[64 + (ledCount * 30)];
        _lastWrite = Stopwatch.GetTimestamp();
        return ValueTask.CompletedTask;
    }

    public bool Write(ReadOnlySpan<byte> colors) => WriteLine(LineKind.Bytes, colors.Length / 3, colors, default);

    public bool WriteFloat(ReadOnlySpan<Vector3> colors, TimeSpan frameInterval)
    {
        if (!_rgbw.Enabled)
        {
            return WriteLine(LineKind.Floats, colors.Length, default, colors);
        }

        if (_rgbwBuffer.Length != colors.Length * 4)
        {
            _rgbwBuffer = new byte[colors.Length * 4];
        }

        _encoder.Render(colors, (int)frameInterval.TotalMilliseconds, _rgbw, _options.ColorOrder, _rgbwBuffer);
        return WriteLine(LineKind.Rgbw, colors.Length, _rgbwBuffer, default);
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        _writer?.Dispose();
        _writer = null;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }

    /// <summary>Formats one RGB output line into <paramref name="line"/> without allocating; returns its length.</summary>
    internal static int FormatLine(Span<char> line, DateTime? timestamp, TimeSpan sinceLast, ReadOnlySpan<byte> colors)
    {
        var n = FormatPrefix(line, timestamp, sinceLast);
        FormatBytes(line, ref n, colors);
        return FormatSuffix(line, n);
    }

    /// <summary>Formats one deep color line (four decimals, 0..255, width 8, as QString::arg(v, 8, 'f', 4)).</summary>
    internal static int FormatFloatLine(Span<char> line, DateTime? timestamp, TimeSpan sinceLast, ReadOnlySpan<Vector3> colors)
    {
        var n = FormatPrefix(line, timestamp, sinceLast);
        FormatFloats(line, ref n, colors);
        return FormatSuffix(line, n);
    }

    /// <summary>Formats one RGBW line (<c>{r,g,b,w}, {r,g,b,w}</c>).</summary>
    internal static int FormatRgbwLine(Span<char> line, DateTime? timestamp, TimeSpan sinceLast, ReadOnlySpan<byte> rgbw)
    {
        var n = FormatPrefix(line, timestamp, sinceLast);
        FormatRgbw(line, ref n, rgbw);
        return FormatSuffix(line, n);
    }

    private static int FormatPrefix(Span<char> line, DateTime? timestamp, TimeSpan sinceLast)
    {
        var n = 0;
        if (timestamp is { } t)
        {
            t.TryFormat(line, out var written, "yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
            n += written;
            Append(line, ref n, " | +");
            sinceLast.TotalMilliseconds.TryFormat(line[n..], out written, "F1", CultureInfo.InvariantCulture);
            n += written;
        }

        Append(line, ref n, " [");
        return n;
    }

    private static int FormatSuffix(Span<char> line, int n)
    {
        line[n++] = ']';
        Append(line, ref n, Environment.NewLine);
        return n;
    }

    private static void FormatBytes(Span<char> line, ref int n, ReadOnlySpan<byte> colors)
    {
        for (var i = 0; i + 2 < colors.Length; i += 3)
        {
            line[n++] = '{';
            AppendByte(line, ref n, colors[i]);
            line[n++] = ',';
            AppendByte(line, ref n, colors[i + 1]);
            line[n++] = ',';
            AppendByte(line, ref n, colors[i + 2]);
            line[n++] = '}';
        }
    }

    private static void FormatRgbw(Span<char> line, ref int n, ReadOnlySpan<byte> rgbw)
    {
        for (var i = 0; i + 3 < rgbw.Length; i += 4)
        {
            if (i > 0)
            {
                Append(line, ref n, ", ");
            }

            line[n++] = '{';
            for (var c = 0; c < 4; c++)
            {
                if (c > 0)
                {
                    line[n++] = ',';
                }

                AppendByte(line, ref n, rgbw[i + c]);
            }

            line[n++] = '}';
        }
    }

    private static void FormatFloats(Span<char> line, ref int n, ReadOnlySpan<Vector3> colors)
    {
        for (var i = 0; i < colors.Length; i++)
        {
            if (i > 0)
            {
                Append(line, ref n, ", ");
            }

            line[n++] = '{';
            AppendFloat(line, ref n, colors[i].X * 255.0f);
            Append(line, ref n, ", ");
            AppendFloat(line, ref n, colors[i].Y * 255.0f);
            Append(line, ref n, ", ");
            AppendFloat(line, ref n, colors[i].Z * 255.0f);
            line[n++] = '}';
        }
    }

    private static void Append(Span<char> line, ref int n, string text)
    {
        text.AsSpan().CopyTo(line[n..]);
        n += text.Length;
    }

    private static void AppendByte(Span<char> line, ref int n, byte value)
    {
        value.TryFormat(line[n..], out var written, default, CultureInfo.InvariantCulture);
        n += written;
    }

    private static void AppendFloat(Span<char> line, ref int n, float value)
    {
        Span<char> digits = stackalloc char[24];
        ((double)value).TryFormat(digits, out var written, "F4", CultureInfo.InvariantCulture);
        for (var pad = written; pad < 8; pad++)
        {
            line[n++] = ' ';
        }

        digits[..written].CopyTo(line[n..]);
        n += written;
    }

    private bool WriteLine(LineKind kind, int ledCount, ReadOnlySpan<byte> bytes, ReadOnlySpan<Vector3> floats)
    {
        var writer = _writer;
        if (writer is null)
        {
            return false;
        }

        var needed = 64 + (ledCount * 30);
        if (_line.Length < needed)
        {
            _line = new char[needed];
        }

        var now = Stopwatch.GetTimestamp();
        var n = FormatPrefix(_line, _options.PrintTimeStamp ? _clock() : null, Stopwatch.GetElapsedTime(_lastWrite, now));
        switch (kind)
        {
            case LineKind.Floats:
                FormatFloats(_line, ref n, floats);
                break;
            case LineKind.Rgbw:
                FormatRgbw(_line, ref n, bytes);
                break;
            default:
                FormatBytes(_line, ref n, bytes);
                break;
        }

        var length = FormatSuffix(_line, n);
        _lastWrite = now;
        try
        {
            writer.Write(_line.AsSpan(0, length));
            writer.Flush();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private enum LineKind
    {
        Bytes,
        Floats,
        Rgbw,
    }
}
