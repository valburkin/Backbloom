using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Overglow.Settings.Storage;

/// <summary>
/// <see cref="ISettingsStore"/> on <see cref="SettingsDatabase"/>: the pipeline's sections in <c>settings</c>, each
/// controller's <c>device</c> in <c>controller_settings</c>. Reads are served from a validated in-memory copy; every
/// update is written in one transaction before the copy changes, so a failed write changes nothing.
/// </summary>
/// <remarks>
/// Rows of sections this version doesn't know (written by a newer one) are left untouched. A stored section that no
/// longer validates (the schema changed) is repaired as an import would, and the fixes are logged.
/// </remarks>
public sealed partial class SqliteSettingsStore : ISettingsStore
{
    /// <summary>The controller the <c>device</c> section refers to.</summary>
    public const int PrimaryController = 0;

    /// <summary>Meta key of the last time anything was stored (absent while the store only holds defaults).</summary>
    public const string ModifiedKey = "modified_at";

    private readonly SettingsDatabase _db;
    private readonly SettingsSchema _schema;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private JsonObject _sections = [];
    private SortedDictionary<int, ControllerSettings> _controllers = [];

    public SqliteSettingsStore(SettingsDatabase db, SettingsSchema schema, ILogger<SqliteSettingsStore> logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _schema = schema;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        Load();
    }

    public event Action<SettingsChange>? Changed;

    public IReadOnlyList<ControllerSettings> Controllers
    {
        get
        {
            lock (_lock)
            {
                return [.. _controllers.Values.Select(Clone)];
            }
        }
    }

    public JsonObject Snapshot()
    {
        lock (_lock)
        {
            var snapshot = new JsonObject();
            foreach (var section in SettingsSchema.BuiltinSections)
            {
                if (GetLocked(section) is { } value)
                {
                    snapshot[section] = value.DeepClone();
                }
            }

            return snapshot;
        }
    }

    public JsonNode? GetSection(string section)
    {
        lock (_lock)
        {
            return GetLocked(section)?.DeepClone();
        }
    }

    public SettingsContent Export()
    {
        lock (_lock)
        {
            return new SettingsContent(_sections.DeepClone().AsObject(), [.. _controllers.Values.Select(Clone)]);
        }
    }

    public SettingsUpdateResult Update(JsonObject sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var errors = new List<SettingsError>();
        var normalized = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var (section, value) in sections)
        {
            if (_schema.Validate(section, value, errors) is { } result)
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
            changed = normalized.Where(kv => !JsonNode.DeepEquals(GetLocked(kv.Key), kv.Value)).ToDictionary(StringComparer.Ordinal);
            if (changed.Count == 0)
            {
                return new SettingsUpdateResult([], []);
            }

            var now = SettingsDatabase.Timestamp(_time.GetUtcNow());
            if (!TryWrite(
                (c, t) =>
                {
                    foreach (var (section, value) in changed)
                    {
                        if (section == SettingsSchema.ControllerSection)
                        {
                            UpsertControllerSection(c, t, PrimaryController, section, value, now);
                        }
                        else
                        {
                            UpsertSection(c, t, section, value, now);
                        }
                    }

                    SettingsDatabase.SetMeta(c, t, ModifiedKey, now);
                },
                out var failure))
            {
                return failure;
            }

            foreach (var (section, value) in changed)
            {
                if (section == SettingsSchema.ControllerSection)
                {
                    var primary = _controllers[PrimaryController];
                    _controllers[PrimaryController] = primary with { Device = value.DeepClone().AsObject() };
                }
                else
                {
                    _sections[section] = value.DeepClone();
                }
            }
        }

        Changed?.Invoke(new SettingsChange(changed));
        return new SettingsUpdateResult([], [.. changed.Keys]);
    }

    public SettingsUpdateResult Replace(SettingsContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var errors = new List<SettingsError>();
        var sections = new JsonObject();
        foreach (var (section, value) in content.Sections)
        {
            if (section == SettingsSchema.ControllerSection)
            {
                errors.Add(new SettingsError(section, "belongs to a controller"));
            }
            else if (_schema.Validate(section, value, errors) is { } result)
            {
                sections[section] = result;
            }
        }

        foreach (var section in _schema.Sections)
        {
            if (section != SettingsSchema.ControllerSection && !sections.ContainsKey(section) && _schema.CreateDefault(section) is { } fallback)
            {
                sections[section] = fallback;
            }
        }

        var controllers = new SortedDictionary<int, ControllerSettings>();
        foreach (var controller in content.Controllers)
        {
            var device = _schema.Validate(SettingsSchema.ControllerSection, controller.Device, errors) as JsonObject;
            if (!controllers.TryAdd(controller.Id, controller with { Device = device ?? [] }))
            {
                errors.Add(new SettingsError($"controllers.{controller.Id}", "is listed twice"));
            }
        }

        if (!controllers.ContainsKey(PrimaryController))
        {
            errors.Add(new SettingsError("controllers", $"must include controller {PrimaryController}"));
        }

        if (errors.Count > 0)
        {
            return SettingsUpdateResult.Rejected(errors);
        }

        Dictionary<string, JsonNode> changed;
        lock (_lock)
        {
            changed = sections.Where(kv => !JsonNode.DeepEquals(_sections[kv.Key], kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value!.DeepClone(), StringComparer.Ordinal);
            var device = controllers[PrimaryController].Device;
            if (!JsonNode.DeepEquals(_controllers[PrimaryController].Device, device))
            {
                changed[SettingsSchema.ControllerSection] = device.DeepClone();
            }

            var now = SettingsDatabase.Timestamp(_time.GetUtcNow());
            if (!TryWrite(
                (c, t) =>
                {
                    SettingsDatabase.Execute(c, t, "DELETE FROM settings");
                    SettingsDatabase.Execute(c, t, "DELETE FROM controllers");
                    foreach (var (section, value) in sections)
                    {
                        UpsertSection(c, t, section, value!, now);
                    }

                    foreach (var controller in controllers.Values)
                    {
                        InsertController(c, t, controller, now);
                    }

                    SettingsDatabase.SetMeta(c, t, ModifiedKey, now);
                },
                out var failure))
            {
                return failure;
            }

            _sections = sections;
            _controllers = controllers;
        }

        if (changed.Count > 0)
        {
            Changed?.Invoke(new SettingsChange(changed));
        }

        return new SettingsUpdateResult([], [.. changed.Keys]);
    }

    private static ControllerSettings Clone(ControllerSettings c) => c with { Device = c.Device.DeepClone().AsObject() };

    private static void UpsertSection(SqliteConnection c, SqliteTransaction t, string section, JsonNode value, string now) =>
        SettingsDatabase.Execute(c, t, """
            INSERT INTO settings (section, value, updated_at) VALUES ($section, $value, $now)
            ON CONFLICT (section) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
            """, ("$section", section), ("$value", value.ToJsonString()), ("$now", now));

    private static void UpsertControllerSection(SqliteConnection c, SqliteTransaction t, int controller, string section, JsonNode value, string now) =>
        SettingsDatabase.Execute(c, t, """
            INSERT INTO controller_settings (controller_id, section, value, updated_at) VALUES ($id, $section, $value, $now)
            ON CONFLICT (controller_id, section) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
            """, ("$id", controller), ("$section", section), ("$value", value.ToJsonString()), ("$now", now));

    private static void InsertController(SqliteConnection c, SqliteTransaction t, ControllerSettings controller, string now)
    {
        SettingsDatabase.Execute(c, t, "INSERT INTO controllers (id, name, enabled) VALUES ($id, $name, $enabled)",
            ("$id", controller.Id), ("$name", controller.Name), ("$enabled", controller.Enabled ? 1 : 0));
        UpsertControllerSection(c, t, controller.Id, SettingsSchema.ControllerSection, controller.Device, now);
    }

    private JsonNode? GetLocked(string section) =>
        section == SettingsSchema.ControllerSection ? _controllers[PrimaryController].Device : _sections[section];

    private bool TryWrite(Action<SqliteConnection, SqliteTransaction> write, out SettingsUpdateResult failure)
    {
        try
        {
            _db.Write(write);
            failure = SettingsUpdateResult.Rejected([]);
            return true;
        }
        catch (SqliteException ex)
        {
            LogSaveFailed(ex, _db.FilePath);
            failure = SettingsUpdateResult.Rejected([new SettingsError(string.Empty, $"couldn't save the settings: {ex.Message}")]);
            return false;
        }
    }

    private void Load()
    {
        var (storedSections, storedControllers) = _db.Read(c =>
        {
            var sections = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var command = c.CreateCommand())
            {
                command.CommandText = "SELECT section, value FROM settings";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    sections[reader.GetString(0)] = reader.GetString(1);
                }
            }

            var controllers = new List<(int Id, string Name, bool Enabled, string? Device)>();
            using (var command = c.CreateCommand())
            {
                command.CommandText = """
                    SELECT c.id, c.name, c.enabled, s.value FROM controllers c
                    LEFT JOIN controller_settings s ON s.controller_id = c.id AND s.section = 'device'
                    ORDER BY c.id
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    controllers.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }

            return (sections, controllers);
        });

        foreach (var section in SettingsSchema.BuiltinSections.Where(_schema.HasSection))
        {
            if (section == SettingsSchema.ControllerSection)
            {
                continue;
            }

            var value = storedSections.TryGetValue(section, out var json) ? LoadSection(section, json) : _schema.CreateDefault(section);
            if (value is not null)
            {
                _sections[section] = value;
            }
        }

        foreach (var (id, name, enabled, device) in storedControllers)
        {
            var value = (device is null ? null : LoadSection(SettingsSchema.ControllerSection, device)) ?? _schema.CreateDefault(SettingsSchema.ControllerSection)!;
            _controllers[id] = new ControllerSettings(id, name, enabled, value.AsObject());
        }

        if (!_controllers.ContainsKey(PrimaryController))
        {
            var primary = new ControllerSettings(PrimaryController, "LED controller", true, _schema.CreateDefault(SettingsSchema.ControllerSection)!.AsObject());
            var now = SettingsDatabase.Timestamp(_time.GetUtcNow());
            _db.Write((c, t) => InsertController(c, t, primary, now));
            _controllers[PrimaryController] = primary;
        }
    }

    private JsonNode? LoadSection(string section, string json)
    {
        JsonNode? stored;
        try
        {
            stored = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            LogUnreadableSection(section);
            return _schema.CreateDefault(section);
        }

        var errors = new List<SettingsError>();
        if (_schema.Validate(section, stored, errors) is { } valid && errors.Count == 0)
        {
            return valid;
        }

        var fixes = new List<SettingsError>();
        var repaired = _schema.Repair(section, stored, fixes);
        LogRepairedSection(section, string.Join("; ", fixes));
        return repaired ?? _schema.CreateDefault(section);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't save the settings to {Path}")]
    private partial void LogSaveFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored '{Section}' settings aren't valid JSON; defaults are used")]
    private partial void LogUnreadableSection(string section);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored '{Section}' settings didn't match this version's schema and were repaired: {Fixes}")]
    private partial void LogRepairedSection(string section, string fixes);
}
