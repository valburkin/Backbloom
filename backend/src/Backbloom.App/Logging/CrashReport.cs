using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Backbloom.App.Logging;

/// <summary>
/// Records why the app couldn't start or ended on an unexpected error in the day's log file
/// (<c>logs/backbloom-yyyyMMdd.log</c>), where the user looks first. The line is appended directly rather than
/// through the logging pipeline, which may not exist yet or may not get to write its queue before the process ends.
/// </summary>
internal static class CrashReport
{
    /// <summary>Appends a <c>FATAL</c> entry with <paramref name="exception"/>; returns the file, or null when it can't be written.</summary>
    public static string? Write(string dataDirectory, string message, Exception exception, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(exception);
        var now = (time ?? TimeProvider.System).GetLocalNow();
        try
        {
            var directory = Path.Combine(dataDirectory, BackbloomHost.LogDirectoryName);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"backbloom-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

            // shared like the log writer's own handle, so this works while it still has the file open
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(RollingFileLoggerProvider.Format(now, LogLevel.Critical, "Backbloom", message, exception));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
