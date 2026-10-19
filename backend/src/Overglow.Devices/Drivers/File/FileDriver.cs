using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Overglow.Devices.Drivers.File;

/// <summary>Settings of the "file" debug driver (sources/led-drivers/schemas/schema-file.json).</summary>
public sealed record FileDriverOptions(string Output, bool PrintTimeStamp = true)
{
    public static FileDriverOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new FileDriverOptions(DeviceConfig.GetString(config, "output"), DeviceConfig.GetBool(config, "printTimeStamp", true));
    }

    /// <summary>The file actually written: "/dev/null" or an empty setting mean the platform's null device.</summary>
    public string ResolvedPath =>
        string.IsNullOrWhiteSpace(Output) || Output == "/dev/null" ? (OperatingSystem.IsWindows() ? @"\\.\NUL" : "/dev/null") : Output;
}

/// <summary>
/// Writes every frame as a text line, e.g. <c>2026-10-19T20:15:00.123 | +16.7 [{255,0,0}{0,0,0}]</c>. Port of
/// <c>DriverOtherFile</c> (sources/led-drivers/other/DriverOtherFile.cpp), 8-bit output only.
/// </summary>
public sealed class FileDriver : ILedDriver
{
    public const string TypeName = "file";

    private readonly FileDriverOptions _options;
    private readonly Func<DateTime> _clock;
    private char[] _line = [];
    private StreamWriter? _writer;
    private long _lastWrite;

    public FileDriver(FileDriverOptions options, Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
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

    public ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        var stream = new FileStream(_options.ResolvedPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 64 * 1024);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
        _line = new char[64 + (ledCount * 14)];
        _lastWrite = Stopwatch.GetTimestamp();
        return ValueTask.CompletedTask;
    }

    public bool Write(ReadOnlySpan<byte> colors)
    {
        var writer = _writer;
        if (writer is null)
        {
            return false;
        }

        var needed = 64 + (colors.Length / 3 * 14);
        if (_line.Length < needed)
        {
            _line = new char[needed];
        }

        var now = Stopwatch.GetTimestamp();
        var length = FormatLine(_line, _options.PrintTimeStamp ? _clock() : null, Stopwatch.GetElapsedTime(_lastWrite, now), colors);
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

    /// <summary>Formats one output line into <paramref name="line"/> without allocating; returns its length.</summary>
    internal static int FormatLine(Span<char> line, DateTime? timestamp, TimeSpan sinceLast, ReadOnlySpan<byte> colors)
    {
        var n = 0;
        if (timestamp is { } t)
        {
            t.TryFormat(line, out var written, "yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            n += written;
            Append(line, ref n, " | +");
            sinceLast.TotalMilliseconds.TryFormat(line[n..], out written, "F1", System.Globalization.CultureInfo.InvariantCulture);
            n += written;
        }

        Append(line, ref n, " [");
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

        line[n++] = ']';
        Append(line, ref n, Environment.NewLine);
        return n;
    }

    private static void Append(Span<char> line, ref int n, string text)
    {
        text.AsSpan().CopyTo(line[n..]);
        n += text.Length;
    }

    private static void AppendByte(Span<char> line, ref int n, byte value)
    {
        value.TryFormat(line[n..], out var written, default, System.Globalization.CultureInfo.InvariantCulture);
        n += written;
    }
}
