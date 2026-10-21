using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Backbloom.App.Logging;

/// <summary>
/// Records why the app couldn't start or ended on an unexpected error in the day's log file
/// (<c>logs/backbloom-yyyyMMdd.log</c>), where the user looks first. The line is appended directly rather than
/// through the logging pipeline, which may not exist yet or may not get to write its queue before the process ends.
/// The performance samples of the last minutes go to <c>logs/crash-yyyyMMdd-HHmmss.jsonl</c> (the newest
/// <see cref="RetainedPerformanceFiles"/> are kept), which the entry names; they are written after the entry, so a
/// failure there (the process may be out of memory) never costs the entry.
/// </summary>
internal static class CrashReport
{
    public const int RetainedPerformanceFiles = 10;

    /// <summary>
    /// Appends a <c>FATAL</c> entry with <paramref name="exception"/> (and writes <paramref name="recentPerformance"/>,
    /// the <see cref="Engine.Diagnostics.PerformanceRecorder"/>'s lines, when there are any); returns the log file,
    /// or null when it can't be written.
    /// </summary>
    public static string? Write(string dataDirectory, string message, Exception exception, IReadOnlyList<string>? recentPerformance = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(exception);
        var now = (time ?? TimeProvider.System).GetLocalNow();
        var directory = Path.Combine(dataDirectory, BackbloomHost.LogDirectoryName);
        var performance = recentPerformance is { Count: > 0 } ? $"crash-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.jsonl" : null;
        string path;
        try
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"backbloom-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

            // shared like the log writer's own handle, so this works while it still has the file open
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            var entry = performance is null ? message : $"{message} (performance before it: {performance})";
            writer.WriteLine(RollingFileLoggerProvider.Format(now, LogLevel.Critical, "Backbloom", entry, exception));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (performance is not null)
        {
            WritePerformance(Path.Combine(directory, performance), recentPerformance!);
        }

        return path;
    }

    private static void WritePerformance(string path, IReadOnlyList<string> lines)
    {
        try
        {
            using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(false)))
            {
                foreach (var line in lines)
                {
                    writer.Write(line);
                    writer.Write('\n');
                }
            }

            PerformanceFile.DeleteOldFiles(Path.GetDirectoryName(path)!, "crash-*.jsonl", RetainedPerformanceFiles);
        }
        catch (Exception)
        {
            // the entry is written; the samples are a bonus
        }
    }
}
