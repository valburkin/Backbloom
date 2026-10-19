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
/// Standard JSON Schema validators don't support the draft-03 <c>required: true</c> form these schemas use, so the
/// checker is kept in house.
/// </summary>
/// <remarks>
/// <see cref="Validate"/> never replaces invalid values: every problem is reported. Only <em>missing</em> required
/// properties are filled from their schema default, so a client can send a section written against an older schema.
/// <see cref="Repair"/> is for data that can't be sent back (imported databases and backups): it fixes what it can
/// and reports each fix.
/// </remarks>
public static class SchemaValidator
{
    /// <summary>Returns a validated copy of <paramref name="value"/> with missing defaults filled in.</summary>
    public static JsonNode? Validate(JsonObject schema, JsonNode? value, string path, List<SettingsError> errors)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(errors);
        return Check(schema, value, path, errors, repair: false);
    }

    /// <summary>
    /// Returns a copy in which values of the wrong type or outside an enum are replaced by their default, numbers
    /// outside their range are clamped, lists of the wrong length are reset to their default and unknown properties
    /// are dropped. Every fix is added to <paramref name="fixes"/>. The result can still be invalid when a value
    /// without a default is missing; validate it before storing it.
    /// </summary>
    public static JsonNode? Repair(JsonObject schema, JsonNode? value, string path, List<SettingsError> fixes)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(fixes);
        return Check(schema, value, path, fixes, repair: true);
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

    private static JsonNode? Check(JsonObject schema, JsonNode? value, string path, List<SettingsError> errors, bool repair)
    {
        var type = (schema["type"] as JsonValue)?.GetValue<string>();
        if (!CheckType(type, value, path, errors))
        {
            return repair ? Fallback(schema, path, errors) : value?.DeepClone();
        }

        if (!CheckEnum(schema, value, path, errors) && repair)
        {
            return Fallback(schema, path, errors);
        }

        switch (value)
        {
            case JsonObject o:
                return CheckObject(schema, o, path, errors, repair);
            case JsonArray a:
                return CheckArray(schema, a, path, errors, repair);
            case JsonValue v:
                return CheckScalar(schema, Canonical(v), path, errors, repair);
            default:
                return null;
        }
    }

    private static JsonObject CheckObject(JsonObject schema, JsonObject value, string path, List<SettingsError> errors, bool repair)
    {
        var result = new JsonObject();
        var properties = schema["properties"] as JsonObject;
        var additional = schema["additionalProperties"] is not JsonValue av || !av.TryGetValue(out bool allowed) || allowed;

        foreach (var (name, child) in value)
        {
            var childPath = Join(path, name);
            if (properties?[name] is JsonObject childSchema)
            {
                var checkedChild = Check(childSchema, child, childPath, errors, repair);
                if (checkedChild is not null || !repair)
                {
                    result[name] = checkedChild;
                }
            }
            else if (additional)
            {
                result[name] = Reparse(child);
            }
            else
            {
                errors.Add(new SettingsError(childPath, repair ? "isn't a known setting; removed" : "isn't a known setting"));
            }
        }

        if (properties is not null)
        {
            foreach (var (name, childSchema) in properties)
            {
                if (result.ContainsKey(name) || (value.ContainsKey(name) && !repair) || childSchema is not JsonObject cs || !IsRequired(cs))
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

    private static JsonArray CheckArray(JsonObject schema, JsonArray value, string path, List<SettingsError> errors, bool repair)
    {
        var countOk = true;
        if (ReadInt(schema, "minItems") is { } min && value.Count < min)
        {
            errors.Add(new SettingsError(path, $"needs at least {min} item{(min == 1 ? "" : "s")}"));
            countOk = false;
        }

        if (ReadInt(schema, "maxItems") is { } max && value.Count > max)
        {
            errors.Add(new SettingsError(path, $"can have at most {max} item{(max == 1 ? "" : "s")}"));
            countOk = false;
        }

        if (!countOk && repair)
        {
            return Fallback(schema, path, errors) as JsonArray ?? [];
        }

        var result = new JsonArray();
        var itemSchema = schema["items"] as JsonObject;
        for (var i = 0; i < value.Count; i++)
        {
            result.Add(itemSchema is null ? Reparse(value[i]) : Check(itemSchema, value[i], Join(path, i.ToString(CultureInfo.InvariantCulture)), errors, repair));
        }

        return result;
    }

    private static JsonValue CheckScalar(JsonObject schema, JsonValue value, string path, List<SettingsError> errors, bool repair)
    {
        if (value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out double number))
        {
            if (ReadDouble(schema, "minimum") is { } min && number < min)
            {
                errors.Add(new SettingsError(path, repair ? $"was below {Format(min)}; set to {Format(min)}" : $"must be at least {Format(min)}"));
                return repair ? Canonical(JsonValue.Create(min)) : value;
            }

            if (ReadDouble(schema, "maximum") is { } max && number > max)
            {
                errors.Add(new SettingsError(path, repair ? $"was above {Format(max)}; set to {Format(max)}" : $"must be at most {Format(max)}"));
                return repair ? Canonical(JsonValue.Create(max)) : value;
            }
        }
        else if (value.TryGetValue(out string? text))
        {
            var count = errors.Count;
            if (ReadInt(schema, "minLength") is { } min && text.Length < min)
            {
                errors.Add(new SettingsError(path, $"must be at least {min} characters long"));
            }

            if (ReadInt(schema, "maxLength") is { } max && text.Length > max)
            {
                errors.Add(new SettingsError(path, $"must be at most {max} characters long"));
            }

            if (errors.Count > count && repair && Fallback(schema, path, errors) is JsonValue fallback)
            {
                return fallback;
            }
        }

        return value;
    }

    /// <summary>The schema default for a repaired value (null: no default, the value is dropped).</summary>
    private static JsonNode? Fallback(JsonObject schema, string path, List<SettingsError> errors)
    {
        var fallback = CreateDefault(schema);
        errors.Add(new SettingsError(path, fallback is null ? "removed (no default)" : "reset to its default"));
        return fallback is JsonValue v ? Canonical(v) : fallback;
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

    private static bool CheckEnum(JsonObject schema, JsonNode? value, string path, List<SettingsError> errors)
    {
        if (schema["enum"] is not JsonArray options || options.Any(o => JsonNode.DeepEquals(o, value)))
        {
            return true;
        }

        errors.Add(new SettingsError(path, $"must be one of: {string.Join(", ", options.Select(o => o?.ToJsonString()))}"));
        return false;
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
