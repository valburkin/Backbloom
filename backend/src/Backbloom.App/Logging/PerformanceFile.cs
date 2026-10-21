using System.Globalization;
using System.Text;

namespace Backbloom.App.Logging;

/// <summary>
/// Appends the <see cref="Engine.Diagnostics.PerformanceRecorder"/>'s samples to
/// <c>logs/performance-yyyyMMdd.jsonl</c> in the data directory (one JSON object per line, a new file per day; only the
/// newest <see cref="RetainedFiles"/> are kept). Each sample opens, appends and closes the file, so it can be read or
/// copied while the app runs and nothing stays half-written.
/// </summary>
public sealed class PerformanceFile
{
    public const int RetainedFiles = 14;
    public const string FilePattern = "performance-*.jsonl";

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private string? _day;

    public PerformanceFile(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The file of the last sample (null before the first one).</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>Appends one sample; a full or unwritable disk only skips it.</summary>
    public void Append(string sample)
    {
        lock (_lock)
        {
            try
            {
                var day = _time.GetLocalNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                var path = Path.Combine(_directory, $"performance-{day}.jsonl");
                if (day != _day)
                {
                    Directory.CreateDirectory(_directory);
                }

                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(sample);
                    writer.Write('\n');
                }

                CurrentPath = path;

                // after the sample is written, so a file that can't be deleted never costs one
                if (day != _day)
                {
                    DeleteOldFiles(_directory, FilePattern, RetainedFiles - 1, keep: path);
                    _day = day;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the next sample tries again
            }
        }
    }

    /// <summary>Deletes all but the newest <paramref name="retained"/> files matching <paramref name="pattern"/> (and <paramref name="keep"/>).</summary>
    internal static void DeleteOldFiles(string directory, string pattern, int retained, string? keep = null)
    {
        var old = new DirectoryInfo(directory).GetFiles(pattern)
            .Where(f => keep is null || !string.Equals(f.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(retained);
        foreach (var file in old)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // still open somewhere, or read-only: next time
            }
        }
    }
}
