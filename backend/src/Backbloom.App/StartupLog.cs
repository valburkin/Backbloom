using Microsoft.Extensions.Logging;

namespace Backbloom.App;

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Backbloom {Version} started. Data directory: {DataDirectory}; web UI: {Ui}; open {Url}")]
    public static partial void Started(ILogger logger, string version, string dataDirectory, string ui, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backbloom stopped")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stopping Backbloom failed")]
    public static partial void StopFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The previous settings were saved to {Path}")]
    public static partial void PreviousSettingsSaved(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't import {Source}")]
    public static partial void ImportFailed(ILogger logger, Exception exception, string source);
}
