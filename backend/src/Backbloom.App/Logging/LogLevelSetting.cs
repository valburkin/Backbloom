using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Backbloom.Settings;

namespace Backbloom.App.Logging;

/// <summary>
/// The minimum log level from the <c>logger</c> settings section (port of AppDaemon's LOGGER handling), applied to
/// every log output and changed at once when the setting changes. "silent" still records errors (C++ logged nothing),
/// so a failure can be diagnosed from the log file.
/// </summary>
public sealed class LogLevelSetting
{
    private volatile int _minimum = (int)LogLevel.Information;

    public LogLevel Minimum => (LogLevel)_minimum;

    public static LogLevel FromSetting(string? level) => level switch
    {
        "silent" => LogLevel.Error,
        "warn" => LogLevel.Warning,
        "verbose" => LogLevel.Information,
        "debug" => LogLevel.Debug,
        _ => LogLevel.Information,
    };

    public void Apply(JsonNode? logger) =>
        _minimum = (int)FromSetting(logger?["level"] is JsonValue v && v.TryGetValue(out string? s) ? s : null);

    /// <summary>Applies the stored level now and every change of it.</summary>
    public void Follow(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Apply(settings.GetSection("logger"));
        settings.Changed += change =>
        {
            if (change.Sections.TryGetValue("logger", out var logger))
            {
                Apply(logger);
            }
        };
    }
}
