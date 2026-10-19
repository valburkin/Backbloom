using System.Reflection;
using System.Text.Json.Nodes;

namespace Overglow.Settings;

/// <summary>
/// The schemas of every settings section this backend supports (copied from HyperHDR's sources/base/schema), plus the
/// per-driver schemas merged into the <c>device</c> section by its <c>type</c>.
/// </summary>
public sealed class SettingsSchema
{
    /// <summary>Sections the slice supports, in the order they are reported.</summary>
    public static readonly IReadOnlyList<string> BuiltinSections = ["general", "logger", "device", "color", "leds", "layout", "network"];

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

    /// <summary>Default value of a section, or null for optional sections that start out absent.</summary>
    public JsonNode? CreateDefault(string section)
    {
        if (s_optionalSections.Contains(section) || !_sections.TryGetValue(section, out var schema))
        {
            return null;
        }

        if (section == "device")
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
        if (!_sections.TryGetValue(section, out var schema))
        {
            errors.Add(new SettingsError(section, "isn't a setting this version supports"));
            return null;
        }

        if (section == "device")
        {
            schema = DeviceSchema(value, errors);
        }

        return SchemaValidator.Validate(schema, value, section, errors);
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
        var resource = $"Overglow.Settings.Schemas.{section}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource) ??
            throw new InvalidOperationException($"Missing embedded schema {resource}.");
        return JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidOperationException($"Empty schema {resource}.");
    }

    // The base device schema allows any extra property; the driver schema of the selected type adds its own rules.
    private JsonObject DeviceSchema(JsonNode? value, List<SettingsError> errors)
    {
        var schema = _sections["device"].DeepClone().AsObject();
        var type = (value as JsonObject)?["type"] is JsonValue tv && tv.TryGetValue(out string? t) ? t : null;
        if (type is null)
        {
            return schema;
        }

        if (!_deviceSchemas.TryGetValue(type, out var driverSchema))
        {
            errors.Add(new SettingsError("device.type", $"unknown LED device type \"{type}\" (available: {string.Join(", ", _deviceSchemas.Keys.Order(StringComparer.Ordinal))})"));
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
