using Microsoft.Extensions.Logging;

namespace Overglow.App;

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Overglow started. Data directory: {DataDirectory}; web UI: {Ui}; listening on {Url}")]
    public static partial void Started(ILogger logger, string dataDirectory, string ui, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "The previous settings were saved to {Path}")]
    public static partial void PreviousSettingsSaved(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't import {Source}")]
    public static partial void ImportFailed(ILogger logger, Exception exception, string source);
}
