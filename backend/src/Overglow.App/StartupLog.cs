using Microsoft.Extensions.Logging;

namespace Overglow.App;

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Overglow started. Data directory: {DataDirectory}; web UI: {Ui}; listening on {Url}")]
    public static partial void Started(ILogger logger, string dataDirectory, string ui, string url);
}
