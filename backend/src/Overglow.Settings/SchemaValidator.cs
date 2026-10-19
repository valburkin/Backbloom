using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Overglow.Settings;

/// <summary>One rejected value: dotted path (array indexes as numbers, e.g. <c>color.channelAdjustment.0.gamma</c>) and why.</summary>
public sealed record SettingsError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>
/// Validates settings against the HyperHDR settings schemas (a draft-03 style subset: <c>type</c>,
/// <c>required: true</c>, <c>properties</c>, <c>additionalProperties</c>, <c>items</c>, <c>enum</c>,
/// <c>minimum</c>/<c>maximum</c>, <c>minLength</c>/<c>maxLength</c>, <c>minItems</c>/<c>maxItems</c>).
/// </summary>
/// <remarks>
/// Unlike the C++ checker, invalid values are never replaced by defaults: every problem is reported. Only
/// <em>missing</em> required properties are filled from their schema default, so a client can send a section
/// written against an older schema.
/// </remarks>
public static class SchemaValidator
{
    /// <summary>Returns a validated copy of <paramref name="value"/> with missing defaults filled in.</summary>
    public static JsonNode? Validate(JsonObject schema, JsonNode? value, string path, List<SettingsError> errors)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(errors);
        var type = (schema["type"] as JsonValue)?.GetValue<string>();
        if (!CheckType(type, value, path, errors))
        {
            return value?.DeepClone();
        }

        CheckEnum(schema, value, path, errors);

        switch (value)
        {
            case JsonObject o:
                return ValidateObject(schema, o, path, errors);
            case JsonArray a:
                return ValidateArray(schema, a, path, errors);
            case JsonValue v:
                var canonical = Canonical(v);
                CheckScalar(schema, canonical, path, errors);
                return canonical;
            default:
                return null;
        }
    }

    /// <summary>Default value of a schema node: its <c>default</c>, else an object of its properties' defaults.</summary>
    public static JsonNode? CreateDefault(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema["default"] is { } d)
        {
            return d.DeepClone();
        }

        var type = (schema["type"] as JsonValue)?.GetValue<string>();
        switch (type)
        {
            case "object":
                var result = new JsonObject();
                if (schema["properties"] is JsonObject properties)
                {
                    foreach (var (name, propertySchema) in properties)
                    {
                        if (propertySchema is JsonObject ps && CreateDefault(ps) is { } pv)
                        {
                            result[name] = pv;
                        }
                    }
                }

                return result;

            case "array":
                var array = new JsonArray();
                var minItems = ReadInt(schema, "minItems") ?? 0;
                if (schema["items"] is JsonObject itemSchema)
                {
                    for (var i = 0; i < minItems; i++)
                    {
                        array.Add(CreateDefault(itemSchema));
                    }
                }

                return array;

            default:
                return null;
        }
    }

    private static JsonObject ValidateObject(JsonObject schema, JsonObject value, string path, List<SettingsError> errors)
    {
        var result = new JsonObject();
        var properties = schema["properties"] as JsonObject;
        var additional = schema["additionalProperties"] is not JsonValue av || !av.TryGetValue(out bool allowed) || allowed;

        foreach (var (name, child) in value)
        {
            var childPath = Join(path, name);
            if (properties?[name] is JsonObject childSchema)
            {
                result[name] = Validate(childSchema, child, childPath, errors);
            }
            else if (additional)
            {
                result[name] = Reparse(child);
            }
            else
            {
                errors.Add(new SettingsError(childPath, "isn't a known setting"));
            }
        }

        if (properties is not null)
        {
            foreach (var (name, childSchema) in properties)
            {
                if (value.ContainsKey(name) || childSchema is not JsonObject cs || !IsRequired(cs))
                {
                    continue;
                }

                if (CreateDefault(cs) is { } fallback)
                {
                    result[name] = fallback;
                }
                else
                {
                    errors.Add(new SettingsError(Join(path, name), "is required"));
                }
            }
        }

        return result;
    }

    private static JsonArray ValidateArray(JsonObject schema, JsonArray value, string path, List<SettingsError> errors)
    {
        if (ReadInt(schema, "minItems") is { } min && value.Count < min)
        {
            errors.Add(new SettingsError(path, $"needs at least {min} item{(min == 1 ? "" : "s")}"));
        }

        if (ReadInt(schema, "maxItems") is { } max && value.Count > max)
        {
            errors.Add(new SettingsError(path, $"can have at most {max} item{(max == 1 ? "" : "s")}"));
        }

        var result = new JsonArray();
        var itemSchema = schema["items"] as JsonObject;
        for (var i = 0; i < value.Count; i++)
        {
            result.Add(itemSchema is null ? Reparse(value[i]) : Validate(itemSchema, value[i], Join(path, i.ToString(CultureInfo.InvariantCulture)), errors));
        }

        return result;
    }

    private static bool CheckType(string? type, JsonNode? value, string path, List<SettingsError> errors)
    {
        var kind = value?.GetValueKind() ?? JsonValueKind.Null;
        if (value is JsonValue v)
        {
            value = Canonical(v);
        }

        var ok = type switch
        {
            null => true,
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            "string" => kind == JsonValueKind.String,
            "boolean" or "bool" => kind is JsonValueKind.True or JsonValueKind.False,
            "number" => kind == JsonValueKind.Number,
            "integer" => kind == JsonValueKind.Number && value!.AsValue().TryGetValue(out double d) && Math.Floor(d) == d,
            _ => true,
        };

        if (!ok)
        {
            errors.Add(new SettingsError(path, type switch
            {
                "object" => "must be an object",
                "array" => "must be a list",
                "string" => "must be text",
                "boolean" or "bool" => "must be true or false",
                "integer" => "must be a whole number",
                _ => "must be a number",
            }));
        }

        return ok;
    }

    private static void CheckEnum(JsonObject schema, JsonNode? value, string path, List<SettingsError> errors)
    {
        if (schema["enum"] is not JsonArray options)
        {
            return;
        }

        if (!options.Any(o => JsonNode.DeepEquals(o, value)))
        {
            errors.Add(new SettingsError(path, $"must be one of: {string.Join(", ", options.Select(o => o?.ToJsonString()))}"));
        }
    }

    private static void CheckScalar(JsonObject schema, JsonValue value, string path, List<SettingsError> errors)
    {
        if (value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out double number))
        {
            if (ReadDouble(schema, "minimum") is { } min && number < min)
            {
                errors.Add(new SettingsError(path, $"must be at least {Format(min)}"));
            }

            if (ReadDouble(schema, "maximum") is { } max && number > max)
            {
                errors.Add(new SettingsError(path, $"must be at most {Format(max)}"));
            }
        }
        else if (value.TryGetValue(out string? text))
        {
            if (ReadInt(schema, "minLength") is { } min && text.Length < min)
            {
                errors.Add(new SettingsError(path, $"must be at least {min} characters long"));
            }

            if (ReadInt(schema, "maxLength") is { } max && text.Length > max)
            {
                errors.Add(new SettingsError(path, $"must be at most {max} characters long"));
            }
        }
    }

    /// <summary>
    /// Re-parses a scalar so it is backed by a JsonElement: values created in code (e.g. <c>node["x"] = 5</c>) only
    /// convert to their own CLR type, while parsed ones convert to any compatible type, which every reader relies on.
    /// </summary>
    private static JsonValue Canonical(JsonValue value) => JsonNode.Parse(value.ToJsonString())!.AsValue();

    private static JsonNode? Reparse(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

    private static bool IsRequired(JsonObject schema) => schema["required"] is JsonValue v && v.TryGetValue(out bool b) && b;

    private static int? ReadInt(JsonObject schema, string name) => ReadDouble(schema, name) is { } d ? (int)d : null;

    private static double? ReadDouble(JsonObject schema, string name) =>
        schema[name] is JsonValue v && v.TryGetValue(out double d) ? d : null;

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";
}
