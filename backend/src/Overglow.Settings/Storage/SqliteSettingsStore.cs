using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Overglow.Settings.Layout;

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

    private const string LayoutSection = "layout";

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
            var (dropLayout, controllers) = PlanLayout(normalized, errors);
            if (errors.Count > 0)
            {
                return SettingsUpdateResult.Rejected(errors);
            }

            changed = normalized.Where(kv => !JsonNode.DeepEquals(GetLocked(kv.Key), kv.Value)).ToDictionary(StringComparer.Ordinal);
            if (changed.Count == 0 && controllers is null && !dropLayout)
            {
                return new SettingsUpdateResult([], []);
            }

            var now = SettingsDatabase.Timestamp(_time.GetUtcNow());
            if (!TryWrite(
                (c, t) =>
                {
                    foreach (var (section, value) in changed)
                    {
                        if (section != SettingsSchema.ControllerSection)
                        {
                            UpsertSection(c, t, section, value, now);
                        }
                        else if (controllers is null)
                        {
                            UpsertControllerSection(c, t, PrimaryController, section, value, now);
                        }
                    }

                    if (controllers is not null)
                    {
                        ReplaceControllers(c, t, controllers, now);
                    }

                    if (dropLayout)
                    {
                        SettingsDatabase.Execute(c, t, "DELETE FROM settings WHERE section = $section", ("$section", LayoutSection));
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

            if (controllers is not null)
            {
                _controllers = controllers;
            }

            if (dropLayout)
            {
                _sections.Remove(LayoutSection);
                LogLayoutRemoved();
            }
        }

        if (changed.Count > 0)
        {
            Changed?.Invoke(new SettingsChange(changed));
        }

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

        if (sections[LayoutSection] is { } layout && errors.Count == 0)
        {
            var byIndex = controllers.Values.ToList();
            var spec = LayoutSpecification.Normalize(layout, _schema, (i, _) => i < byIndex.Count ? byIndex[i].Device : null, errors);
            if (spec is not null)
            {
                var derived = Derive(spec, errors);
                sections[LayoutSection] = derived.Layout;
                sections["leds"] = derived.Leds;
                controllers = derived.Controllers;
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
                    foreach (var (section, value) in sections)
                    {
                        UpsertSection(c, t, section, value!, now);
                    }

                    ReplaceControllers(c, t, controllers, now);

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

    private static void ReplaceControllers(SqliteConnection c, SqliteTransaction t, SortedDictionary<int, ControllerSettings> controllers, string now)
    {
        SettingsDatabase.Execute(c, t, "DELETE FROM controllers");
        foreach (var controller in controllers.Values)
        {
            InsertController(c, t, controller, now);
        }
    }

    private static bool SameControllers(SortedDictionary<int, ControllerSettings> a, SortedDictionary<int, ControllerSettings> b) =>
        a.Count == b.Count && a.Values.Zip(b.Values).All(p =>
            p.First.Id == p.Second.Id && p.First.Name == p.Second.Name && p.First.Enabled == p.Second.Enabled && JsonNode.DeepEquals(p.First.Device, p.Second.Device));

    // Text round trip: values built in code compare and serialize exactly like the ones read back from the database.
    private static JsonNode Canonical(JsonNode node) => JsonNode.Parse(node.ToJsonString())!;

    // A controller keeps its driver settings when a layout without them is saved: by id from the stored layout, or
    // (no layout yet: the first save after a HyperHDR import) the stored controller at the same position.
    private static JsonObject? ExistingDevice(JsonObject? layout, List<ControllerSettings> byIndex, int index, string id)
    {
        if (layout is not null)
        {
            return (layout["controllers"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(c => c["id"] is JsonValue v && v.TryGetValue(out string? s) && s == id)?["device"] as JsonObject;
        }

        return index < byIndex.Count ? byIndex[index].Device : null;
    }

    /// <summary>
    /// The sections and controllers a normalized layout implies: <c>leds</c> is every controller's LEDs one after the
    /// other, the controllers table mirrors the layout's controllers (ids 0, 1, ... in layout order; controller 0's
    /// device is the <c>device</c> section).
    /// </summary>
    private (JsonObject Layout, JsonNode Leds, SortedDictionary<int, ControllerSettings> Controllers) Derive(JsonObject spec, List<SettingsError> errors)
    {
        var layout = Canonical(spec).AsObject();
        var setup = LayoutSpecification.Compile(layout);
        var leds = _schema.Validate("leds", Canonical(setup.AllLeds()), errors) ?? new JsonArray();
        var controllers = new SortedDictionary<int, ControllerSettings>();
        for (var i = 0; i < setup.Controllers.Count; i++)
        {
            var controller = setup.Controllers[i];
            controllers[i] = new ControllerSettings(i, controller.Name, controller.Enabled, controller.Device);
        }

        return (layout, leds, controllers);
    }

    /// <summary>
    /// Keeps the layout, <c>leds</c>, <c>device</c> and the controllers consistent for an update (see the README,
    /// "LED controllers and the layout"); may add derived sections to <paramref name="sections"/>.
    /// </summary>
    /// <returns>Whether the stored layout is removed, and the controllers that replace the current ones (null: unchanged).</returns>
    private (bool DropLayout, SortedDictionary<int, ControllerSettings>? Controllers) PlanLayout(Dictionary<string, JsonNode> sections, List<SettingsError> errors)
    {
        var current = _sections[LayoutSection] as JsonObject;
        if (sections.TryGetValue(LayoutSection, out var incoming))
        {
            var byIndex = _controllers.Values.ToList();
            var spec = LayoutSpecification.Normalize(incoming, _schema, (i, id) => ExistingDevice(current, byIndex, i, id), errors);
            if (spec is null)
            {
                return (false, null);
            }

            if (sections.TryGetValue(SettingsSchema.ControllerSection, out var device))
            {
                spec["controllers"]![0]!["device"] = device.DeepClone();
            }

            var derived = Derive(spec, errors);
            sections[LayoutSection] = derived.Layout;
            sections["leds"] = derived.Leds;
            sections[SettingsSchema.ControllerSection] = derived.Controllers[PrimaryController].Device.DeepClone();
            return (false, SameControllers(derived.Controllers, _controllers) ? null : derived.Controllers);
        }

        if (current is null)
        {
            return (false, null);
        }

        if (sections.TryGetValue("leds", out var leds) && !JsonNode.DeepEquals(leds, _sections["leds"]))
        {
            var count = (current["controllers"] as JsonArray)?.Count ?? 0;
            if (count > 1)
            {
                errors.Add(new SettingsError("leds", $"comes from the layout, which spans {count} controllers; save the layout instead"));
                return (false, null);
            }

            // a single-controller client (HyperHDR's LED editor) takes over: the layout no longer describes the LEDs
            return (true, null);
        }

        if (sections.TryGetValue(SettingsSchema.ControllerSection, out var primaryDevice))
        {
            var spec = current.DeepClone().AsObject();
            spec["controllers"]![0]!["device"] = primaryDevice.DeepClone();
            sections[LayoutSection] = spec;
        }

        return (false, null);
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

        LoadLayout();
    }

    // A stored layout is normalized like a saved one: one written by an older version gets each controller's driver
    // settings from the controllers table (by position), and the derived leds and controllers are brought in line.
    private void LoadLayout()
    {
        if (_sections[LayoutSection] is not { } stored)
        {
            return;
        }

        var errors = new List<SettingsError>();
        var byIndex = _controllers.Values.ToList();
        var spec = LayoutSpecification.Normalize(stored, _schema, (i, _) => i < byIndex.Count ? byIndex[i].Device : null, errors);
        var derived = spec is null ? default : Derive(spec, errors);
        if (spec is null || errors.Count > 0)
        {
            _sections.Remove(LayoutSection);
            LogInvalidLayout(string.Join("; ", errors));
            return;
        }

        _sections[LayoutSection] = derived.Layout;
        _sections["leds"] = derived.Leds;
        if (!SameControllers(derived.Controllers, _controllers))
        {
            var now = SettingsDatabase.Timestamp(_time.GetUtcNow());
            _db.Write((c, t) => ReplaceControllers(c, t, derived.Controllers, now));
            _controllers = derived.Controllers;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored LED layout isn't valid and is ignored (the LEDs follow the 'leds' and 'device' settings): {Errors}")]
    private partial void LogInvalidLayout(string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "The LED positions were saved without the layout: the layout is removed and the 'leds' setting describes the LEDs")]
    private partial void LogLayoutRemoved();
}
