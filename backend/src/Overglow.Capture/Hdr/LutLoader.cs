using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Overglow.Capture.Hdr;

/// <summary>
/// Loads <c>lut_lin_tables.3d</c> (or its zstd-compressed <c>.3d.zst</c> variant). Port of LutLoader::load: the first
/// file that exists wins; it must hold one table or the legacy layout of three tables (HDR RGB, HDR YUV, YUV), of which
/// screen capture uses the first (HDR RGB) one. A compressed file is decompressed up to the first table.
/// </summary>
public static partial class LutLoader
{
    public const string FileName = "lut_lin_tables.3d";

    public const string CompressedSuffix = ".zst";

    /// <summary>Tries the files in order; returns null when none exists or the first existing one is invalid.</summary>
    public static ToneMappingLut? Load(IEnumerable<string> files, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(logger);
        foreach (var file in files)
        {
            var compressed = file + CompressedSuffix;
            if (File.Exists(file))
            {
                return LoadFile(file, compressed: false, logger);
            }

            if (File.Exists(compressed))
            {
                return LoadFile(compressed, compressed: true, logger);
            }

            LogNotFound(logger, file);
        }

        return null;
    }

    private static ToneMappingLut? LoadFile(string path, bool compressed, ILogger logger)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, FileOptions.SequentialScan);
            if (!compressed && stream.Length != ToneMappingLut.TableSize && stream.Length != 3L * ToneMappingLut.TableSize)
            {
                LogInvalidLength(logger, path, stream.Length, ToneMappingLut.TableSize);
                return null;
            }

            var table = GC.AllocateUninitializedArray<byte>(ToneMappingLut.TableSize);
            if (compressed)
            {
                using var decompressor = new DecompressionStream(stream);
                decompressor.ReadExactly(table);
            }
            else
            {
                stream.ReadExactly(table);
            }

            LogLoaded(logger, path);
            return new ToneMappingLut(table, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ZstdException)
        {
            LogReadFailed(logger, ex, path);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "LUT file is not here: {Path}")]
    private static partial void LogNotFound(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "LUT file {Path} has an invalid length: {Length} bytes (expected {TableSize} or three times that)")]
    private static partial void LogInvalidLength(ILogger logger, string path, long length, int tableSize);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded the HDR tone mapping LUT {Path}")]
    private static partial void LogLoaded(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read the LUT file {Path}")]
    private static partial void LogReadFailed(ILogger logger, Exception exception, string path);
}
