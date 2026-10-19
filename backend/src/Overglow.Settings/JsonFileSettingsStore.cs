using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Overglow.Settings;

/// <summary>
/// Settings kept in <c>settings.json</c> in the data directory, written atomically (temp file + rename) on every change.
/// A stand-in for the SQLite store that will also import HyperHDR databases.
/// </summary>
/// <remarks>
/// Sections the schema doesn't know are kept untouched in the file. A stored section that no longer validates is
/// replaced by its default when loading, and the problem is logged.
/// </remarks>
public sealed partial class JsonFileSettingsStore : ISettingsStore
{
    public const string FileName = "settings.json";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    private readonly SettingsSchema _schema;
    private readonly ILogger _logger;
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly JsonObject _values = [];
    private readonly JsonObject _unknown = [];

    public JsonFileSettingsStore(string dataDirectory, SettingsSchema schema, ILogger<JsonFileSettingsStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(logger);
        _schema = schema;
        _logger = logger;
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
        Load();
    }

    public event Action<SettingsChange>? Changed;

    public string FilePath => _path;

    public JsonObject Snapshot()
    {
        lock (_lock)
        {
            return _values.DeepClone().AsObject();
        }
    }

    public JsonNode? GetSection(string section)
    {
        lock (_lock)
        {
            return _values[section]?.DeepClone();
        }
    }

    public SettingsUpdateResult Update(JsonObject sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var errors = new List<SettingsError>();
        var normalized = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var (section, value) in sections)
        {
            var result = _schema.Validate(section, value, errors);
            if (result is not null)
            {
                normalized[section] = result;
            }
        }

        if (errors.Count > 0)
        {
            return SettingsUpdateResult.Rejected(errors);
        }

        Dictionary<string, JsonNode> changed;
        lock (_lock)
        {
            changed = normalized.Where(kv => !JsonNode.DeepEquals(_values[kv.Key], kv.Value)).ToDictionary(StringComparer.Ordinal);
            if (changed.Count == 0)
            {
                return new SettingsUpdateResult([], []);
            }

            var previous = changed.Keys.ToDictionary(k => k, k => _values[k]?.DeepClone(), StringComparer.Ordinal);
            foreach (var (section, value) in changed)
            {
                _values[section] = value.DeepClone();
            }

            try
            {
                Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (var (section, value) in previous)
                {
                    _values[section] = value;
                }

                LogSaveFailed(ex, _path);
                return SettingsUpdateResult.Rejected([new SettingsError(string.Empty, $"couldn't save the settings: {ex.Message}")]);
            }
        }

        Changed?.Invoke(new SettingsChange(changed));
        return new SettingsUpdateResult([], [.. changed.Keys]);
    }

    private void Load()
    {
        JsonObject? stored = null;
        if (File.Exists(_path))
        {
            try
            {
                stored = JsonNode.Parse(File.ReadAllText(_path))?["settings"] as JsonObject;
            }
            catch (JsonException ex)
            {
                var backup = _path + ".invalid";
                File.Copy(_path, backup, overwrite: true);
                LogUnreadable(ex, _path, backup);
            }
        }

        foreach (var section in _schema.Sections)
        {
            var value = stored?[section];
            if (value is not null)
            {
                var errors = new List<SettingsError>();
                var normalized = _schema.Validate(section, value, errors);
                if (errors.Count == 0 && normalized is not null)
                {
                    _values[section] = normalized;
                    continue;
                }

                LogInvalidSection(section, string.Join("; ", errors));
            }

            if (_schema.CreateDefault(section) is { } fallback)
            {
                _values[section] = fallback;
            }
        }

        if (stored is not null)
        {
            foreach (var (section, value) in stored)
            {
                if (!_schema.HasSection(section))
                {
                    _unknown[section] = value?.DeepClone();
                }
            }
        }
    }

    private void Save()
    {
        var settings = _values.DeepClone().AsObject();
        foreach (var (section, value) in _unknown)
        {
            settings[section] = value?.DeepClone();
        }

        var document = new JsonObject { ["version"] = FormatVersion, ["settings"] = settings };
        var temp = _path + ".tmp";
        File.WriteAllText(temp, document.ToJsonString(s_writeOptions));
        File.Move(temp, _path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't save the settings to {Path}")]
    private partial void LogSaveFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "The settings file {Path} isn't valid JSON; it was copied to {Backup} and defaults are used")]
    private partial void LogUnreadable(Exception exception, string path, string backup);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored '{Section}' settings are invalid and were reset to defaults: {Errors}")]
    private partial void LogInvalidSection(string section, string errors);
}
