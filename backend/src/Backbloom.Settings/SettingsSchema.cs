using System.Reflection;
using System.Text.Json.Nodes;

namespace Backbloom.Settings;

/// <summary>
/// The schemas of every settings section (copied unchanged from sources/base/schema of the C++ reference: the kept
/// HyperHDR settings types), plus the per-driver schemas merged into the <c>device</c> section by its <c>type</c>.
/// The schemas are the only place defaults are defined.
/// </summary>
public sealed class SettingsSchema
{
    /// <summary>The section that is stored per controller; every other section belongs to the one pipeline.</summary>
    public const string ControllerSection = "device";

    /// <summary>Every section, in the order of the C++ settings.schema.json (and of <c>getconfig</c>).</summary>
    public static readonly IReadOnlyList<string> BuiltinSections =
    [
        "general", "logger", "device", "color", "smoothing", "systemGrabber", "blackborderdetector", "foregroundEffect",
        "backgroundEffect", "soundEffect", "webConfig", "effects", "systemControl", "network", "ledConfig", "leds", "mqtt",
        "layout",
    ];

    /// <summary>Sections without a default: absent until a client saves them (HyperHDR stored an empty <c>layout</c>).</summary>
    private static readonly HashSet<string> s_optionalSections = ["layout"];

    private readonly Dictionary<string, JsonObject> _sections;
    private readonly IReadOnlyDictionary<string, JsonObject> _deviceSchemas;

    public SettingsSchema(IReadOnlyDictionary<string, JsonObject> sections, IReadOnlyDictionary<string, JsonObject> deviceSchemas)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(deviceSchemas);
        _sections = new Dictionary<string, JsonObject>(sections, StringComparer.Ordinal);
        _deviceSchemas = deviceSchemas;
    }

    /// <summary>The built-in section schemas embedded in this assembly, with the given LED driver schemas.</summary>
    public static SettingsSchema CreateBuiltin(IReadOnlyDictionary<string, JsonObject> deviceSchemas) =>
        new(BuiltinSections.ToDictionary(s => s, LoadEmbedded, StringComparer.Ordinal), deviceSchemas);

    public IEnumerable<string> Sections => _sections.Keys;

    public bool HasSection(string section) => _sections.ContainsKey(section);

    public IEnumerable<string> DeviceTypes => _deviceSchemas.Keys;

    public bool HasDeviceType(string type) => _deviceSchemas.ContainsKey(type);

    /// <summary>Whether a section may be absent (it has no default).</summary>
    public static bool IsOptional(string section) => s_optionalSections.Contains(section);

    /// <summary>Default value of a section, or null for optional sections that start out absent.</summary>
    public JsonNode? CreateDefault(string section)
    {
        if (s_optionalSections.Contains(section) || !_sections.TryGetValue(section, out var schema))
        {
            return null;
        }

        if (section == ControllerSection)
        {
            var device = SchemaValidator.CreateDefault(schema)?.AsObject() ?? [];
            var type = device["type"]?.GetValue<string>();
            if (type is not null && _deviceSchemas.TryGetValue(type, out var driverSchema) &&
                SchemaValidator.CreateDefault(driverSchema) is JsonObject driverDefaults)
            {
                foreach (var (name, value) in driverDefaults)
                {
                    device[name] ??= value?.DeepClone();
                }
            }

            return device;
        }

        return SchemaValidator.CreateDefault(schema);
    }

    /// <summary>Validates one section; returns the normalized copy (missing required values filled in).</summary>
    public JsonNode? Validate(string section, JsonNode? value, List<SettingsError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return SectionSchema(section, value, errors) is { } schema ? SchemaValidator.Validate(schema, value, section, errors) : null;
    }

    /// <summary>
    /// Validates the driver settings of one controller (the <c>device</c> section's rules) found at
    /// <paramref name="path"/>, e.g. inside the <c>layout</c>; returns the normalized copy.
    /// </summary>
    public JsonNode? ValidateDevice(JsonNode? value, string path, List<SettingsError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return SchemaValidator.Validate(DeviceSchema(value, path, errors), value, path, errors);
    }

    /// <summary>
    /// Repairs one imported section (see <see cref="SchemaValidator.Repair"/>); null when the section is unknown or
    /// still invalid afterwards. <paramref name="fixes"/> lists every change and remaining problem.
    /// </summary>
    public JsonNode? Repair(string section, JsonNode? value, List<SettingsError> fixes)
    {
        ArgumentNullException.ThrowIfNull(fixes);
        if (SectionSchema(section, value, fixes) is not { } schema)
        {
            return null;
        }

        var repaired = SchemaValidator.Repair(schema, value, section, fixes);
        var remaining = new List<SettingsError>();
        var valid = Validate(section, repaired, remaining);
        fixes.AddRange(remaining);
        return remaining.Count == 0 ? valid : null;
    }

    /// <summary>The <c>config getschema</c> reply: every section plus <c>properties.alldevices</c>.</summary>
    public JsonObject ToClientSchema()
    {
        var properties = new JsonObject();
        foreach (var (name, schema) in _sections)
        {
            properties[name] = schema.DeepClone();
        }

        var all = new JsonObject();
        foreach (var (type, schema) in _deviceSchemas.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            all[type] = schema.DeepClone();
        }

        properties["alldevices"] = all;
        return new JsonObject
        {
            ["type"] = "object",
            ["required"] = true,
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject LoadEmbedded(string section)
    {
        var resource = $"Backbloom.Settings.Schemas.{section}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource) ??
            throw new InvalidOperationException($"Missing embedded schema {resource}.");
        return JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidOperationException($"Empty schema {resource}.");
    }

    private JsonObject? SectionSchema(string section, JsonNode? value, List<SettingsError> errors)
    {
        if (!_sections.TryGetValue(section, out var schema))
        {
            errors.Add(new SettingsError(section, "isn't a setting this version supports"));
            return null;
        }

        return section == ControllerSection ? DeviceSchema(value, section, errors) : schema;
    }

    // The base device schema allows any extra property; the driver schema of the selected type adds its own rules.
    private JsonObject DeviceSchema(JsonNode? value, string path, List<SettingsError> errors)
    {
        var schema = _sections[ControllerSection].DeepClone().AsObject();
        var type = (value as JsonObject)?["type"] is JsonValue tv && tv.TryGetValue(out string? t) ? t : null;
        if (type is null)
        {
            return schema;
        }

        if (!_deviceSchemas.TryGetValue(type, out var driverSchema))
        {
            errors.Add(new SettingsError($"{path}.type", $"unknown LED device type \"{type}\" (available: {string.Join(", ", _deviceSchemas.Keys.Order(StringComparer.Ordinal))})"));
            return schema;
        }

        var properties = schema["properties"]!.AsObject();
        if (driverSchema["properties"] is JsonObject driverProperties)
        {
            foreach (var (name, propertySchema) in driverProperties)
            {
                properties[name] = propertySchema?.DeepClone();
            }
        }

        return schema;
    }
}
