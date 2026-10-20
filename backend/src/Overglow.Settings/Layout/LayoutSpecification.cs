using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Overglow.Settings.Layout;

/// <summary>
/// The <c>layout</c> setting (the UI's <c>LayoutSpec</c>, <c>ui/src/layout/types.ts</c>): controllers with their
/// outputs and driver settings, and the strips wired to those outputs, made of segments. This class validates it,
/// fills in each controller's <c>device</c>, and compiles it into one sampling area per LED per controller, exactly
/// as <c>compileLayout</c> in <c>ui/src/layout/compile.ts</c> does (the shared fixture
/// <c>tests/fixtures/layouts/ui-multi-controller.json</c> keeps the two equal).
/// </summary>
/// <remarks>
/// Rules: at least one controller; controller ids unique; each controller's outputs have unique indexes, a length of
/// at least 1 and tile its LED index space from 0 without gaps or overlaps, so its LED count is the sum of its
/// outputs' lengths (at most <see cref="MaxLedsPerController"/>); a strip names an existing controller and output,
/// one strip per output, and has at most as many LEDs as its output (fewer: the rest stays dark). Properties the
/// backend doesn't use (the UI's <c>kind</c>, <c>host</c>, <c>pin</c>, ...) are kept as they are.
/// </remarks>
public static class LayoutSpecification
{
    public const int MaxLedsPerController = 10_000;

    private const double DefaultBlend = 1.2;

    private static readonly string[] s_edges = ["top", "bottom", "left", "right"];

    /// <summary>
    /// Validates <paramref name="value"/> and returns the normalized copy the store keeps: every controller has a
    /// validated <c>device</c> (the one it brought, else <paramref name="existingDevice"/>(index, id), else the
    /// default device), <c>name</c> and <c>enabled</c>. Returns null (and adds to <paramref name="errors"/>) when
    /// anything is wrong.
    /// </summary>
    public static JsonObject? Normalize(JsonNode? value, SettingsSchema schema, Func<int, string, JsonObject?> existingDevice, List<SettingsError> errors)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(existingDevice);
        ArgumentNullException.ThrowIfNull(errors);
        const string Root = "layout";
        var before = errors.Count;
        if (value is not JsonObject source)
        {
            errors.Add(new SettingsError(Root, "must be an object"));
            return null;
        }

        var spec = source.DeepClone().AsObject();
        if (!TryInt(spec["version"], out var version) || version != 1)
        {
            errors.Add(new SettingsError($"{Root}.version", "must be 1"));
        }

        if (spec["controllers"] is not JsonArray { Count: > 0 } controllers)
        {
            errors.Add(new SettingsError($"{Root}.controllers", "needs at least one controller"));
            return null;
        }

        var outputsByController = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        for (var i = 0; i < controllers.Count; i++)
        {
            NormalizeController(controllers[i], $"{Root}.controllers.{i}", i, schema, existingDevice, outputsByController, errors);
        }

        var strips = spec["strips"] as JsonArray;
        if (strips is null)
        {
            errors.Add(new SettingsError($"{Root}.strips", "must be a list"));
        }
        else
        {
            var stripIds = new HashSet<string>(StringComparer.Ordinal);
            var usedOutputs = new HashSet<(string, int)>();
            for (var i = 0; i < strips.Count; i++)
            {
                ValidateStrip(strips[i], $"{Root}.strips.{i}", outputsByController, stripIds, usedOutputs, errors);
            }
        }

        return errors.Count == before ? spec : null;
    }

    /// <summary>Compiles a layout that <see cref="Normalize"/> accepted.</summary>
    public static ControllerSetup Compile(JsonObject spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var strips = (spec["strips"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var result = new List<ControllerConfig>();
        foreach (var controller in (spec["controllers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = String(controller["id"]) ?? string.Empty;
            var outputs = Outputs(controller);
            var size = outputs.Count == 0 ? 0 : outputs.Max(o => o.Start + o.Length);
            var leds = new JsonObject?[size];
            var ranges = new List<StripRange>();
            foreach (var strip in strips.Where(s => String(s["controllerId"]) == id))
            {
                var found = TryInt(strip["output"], out var outputIndex) ? outputs.FindIndex(o => o.Index == outputIndex) : -1;
                if (found < 0)
                {
                    continue;
                }

                var output = outputs[found];
                var rects = CompileStrip(strip);
                var n = Math.Min(rects.Count, output.Length);
                for (var i = 0; i < n; i++)
                {
                    leds[output.Start + i] = rects[i];
                }

                var stripId = String(strip["id"]) ?? string.Empty;
                var brightness = TryNumber(strip["brightness"], out var b) ? Math.Clamp(b, 0, 1) : 1.0;
                ranges.Add(new StripRange(stripId, String(strip["name"]) ?? stripId, output.Start, output.Length, brightness));
            }

            var array = new JsonArray();
            foreach (var led in leds)
            {
                array.Add((JsonNode)(led ?? DisabledRect()));
            }

            result.Add(new ControllerConfig(
                id,
                String(controller["name"]) ?? id,
                controller["enabled"] is not JsonValue e || !e.TryGetValue(out bool enabled) || enabled,
                controller["device"] as JsonObject is { } device ? device.DeepClone().AsObject() : [],
                array,
                [.. ranges.OrderBy(r => r.Start)]));
        }

        return new ControllerSetup(result, FromLayout: true);
    }

    /// <summary>Rects of one strip's segments, in wiring order (<c>compileStrip</c>).</summary>
    public static List<JsonObject> CompileStrip(JsonObject strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        var rects = new List<JsonObject>();
        foreach (var segment in (strip["segments"] as JsonArray ?? []).OfType<JsonObject>())
        {
            CompileSegment(segment, rects);
        }

        return rects;
    }

    private static void NormalizeController(
        JsonNode? node,
        string path,
        int index,
        SettingsSchema schema,
        Func<int, string, JsonObject?> existingDevice,
        Dictionary<string, Dictionary<int, int>> outputsByController,
        List<SettingsError> errors)
    {
        if (node is not JsonObject controller)
        {
            errors.Add(new SettingsError(path, "must be an object"));
            return;
        }

        var id = String(controller["id"]);
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add(new SettingsError($"{path}.id", "must be a non-empty text"));
            return;
        }

        if (outputsByController.ContainsKey(id))
        {
            errors.Add(new SettingsError($"{path}.id", $"\"{id}\" is used by another controller"));
            return;
        }

        switch (controller["name"])
        {
            case null:
                controller["name"] = id;
                break;
            case JsonValue nv when nv.TryGetValue(out string? name):
                if (name.Length == 0)
                {
                    controller["name"] = id;
                }

                break;
            default:
                errors.Add(new SettingsError($"{path}.name", "must be a text"));
                break;
        }

        if (controller["enabled"] is null)
        {
            controller["enabled"] = true;
        }
        else if (controller["enabled"] is not JsonValue ev || !ev.TryGetValue(out bool _))
        {
            errors.Add(new SettingsError($"{path}.enabled", "must be true or false"));
        }

        var outputs = new Dictionary<int, int>();
        outputsByController[id] = outputs;
        ValidateOutputs(controller["outputs"], $"{path}.outputs", outputs, errors);

        var device = controller["device"] switch
        {
            null => existingDevice(index, id) ?? schema.CreateDefault(SettingsSchema.ControllerSection) as JsonObject,
            JsonObject given => given,
            _ => null,
        };
        if (device is null)
        {
            errors.Add(new SettingsError($"{path}.device", "must be an object with the LED driver settings"));
            return;
        }

        if (schema.ValidateDevice(device, $"{path}.device", errors) is JsonObject valid)
        {
            controller["device"] = valid.DeepClone();
        }
    }

    private static void ValidateOutputs(JsonNode? node, string path, Dictionary<int, int> lengths, List<SettingsError> errors)
    {
        if (node is not JsonArray { Count: > 0 } outputs)
        {
            errors.Add(new SettingsError(path, "needs at least one output"));
            return;
        }

        var ranges = new List<(int Start, int Length, string Path)>();
        for (var i = 0; i < outputs.Count; i++)
        {
            var p = $"{path}.{i}";
            if (outputs[i] is not JsonObject output)
            {
                errors.Add(new SettingsError(p, "must be an object"));
                continue;
            }

            var ok = true;
            if (!TryInt(output["index"], out var index) || index < 0)
            {
                errors.Add(new SettingsError($"{p}.index", "must be a whole number, at least 0"));
                ok = false;
            }
            else if (lengths.ContainsKey(index))
            {
                errors.Add(new SettingsError($"{p}.index", $"output {index} is listed twice"));
                ok = false;
            }

            if (!TryInt(output["start"], out var start) || start < 0)
            {
                errors.Add(new SettingsError($"{p}.start", "must be a whole number, at least 0"));
                ok = false;
            }

            if (!TryInt(output["length"], out var length) || length < 1)
            {
                errors.Add(new SettingsError($"{p}.length", "must be a whole number, at least 1"));
                ok = false;
            }

            if (ok)
            {
                lengths[index] = length;
                ranges.Add((start, length, p));
            }
        }

        var next = 0;
        foreach (var (start, length, p) in ranges.OrderBy(r => r.Start))
        {
            if (start != next)
            {
                errors.Add(new SettingsError($"{p}.start", start < next
                    ? $"overlaps the previous output (LEDs {start}..{next - 1} are claimed twice)"
                    : $"leaves LEDs {next}..{start - 1} without an output; outputs must follow each other from LED 0"));
            }

            next = Math.Max(next, start + length);
        }

        if (next > MaxLedsPerController)
        {
            errors.Add(new SettingsError(path, $"a controller can drive at most {MaxLedsPerController} LEDs (these outputs have {next})"));
        }
    }

    private static void ValidateStrip(
        JsonNode? node,
        string path,
        Dictionary<string, Dictionary<int, int>> outputsByController,
        HashSet<string> stripIds,
        HashSet<(string, int)> usedOutputs,
        List<SettingsError> errors)
    {
        if (node is not JsonObject strip)
        {
            errors.Add(new SettingsError(path, "must be an object"));
            return;
        }

        var id = String(strip["id"]);
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add(new SettingsError($"{path}.id", "must be a non-empty text"));
        }
        else if (!stripIds.Add(id))
        {
            errors.Add(new SettingsError($"{path}.id", $"\"{id}\" is used by another strip"));
        }

        if (strip["brightness"] is { } brightness && (!TryNumber(brightness, out var b) || b < 0 || b > 1))
        {
            errors.Add(new SettingsError($"{path}.brightness", "must be a number from 0 to 1"));
        }

        var count = 0;
        if (strip["segments"] is not JsonArray segments)
        {
            errors.Add(new SettingsError($"{path}.segments", "must be a list"));
        }
        else
        {
            for (var i = 0; i < segments.Count; i++)
            {
                count += ValidateSegment(segments[i], $"{path}.segments.{i}", errors);
            }
        }

        var controllerId = String(strip["controllerId"]);
        if (controllerId is null || !outputsByController.TryGetValue(controllerId, out var outputs))
        {
            errors.Add(new SettingsError($"{path}.controllerId", $"refers to the controller \"{controllerId}\", which isn't in the layout"));
            return;
        }

        if (!TryInt(strip["output"], out var output) || !outputs.TryGetValue(output, out var length))
        {
            errors.Add(new SettingsError($"{path}.output", $"refers to output {strip["output"]?.ToJsonString() ?? "(none)"}, which controller \"{controllerId}\" doesn't have"));
            return;
        }

        if (!usedOutputs.Add((controllerId, output)))
        {
            errors.Add(new SettingsError($"{path}.output", $"output {output} of controller \"{controllerId}\" already drives another strip"));
        }

        if (count > length)
        {
            errors.Add(new SettingsError($"{path}.segments", $"the strip has {count} LEDs, but its output drives only {length}"));
        }
    }

    // Returns the segment's LED count (0 when it is invalid).
    private static int ValidateSegment(JsonNode? node, string path, List<SettingsError> errors)
    {
        if (node is not JsonObject segment)
        {
            errors.Add(new SettingsError(path, "must be an object"));
            return 0;
        }

        var before = errors.Count;
        if (!TryInt(segment["count"], out var count) || count < 1)
        {
            errors.Add(new SettingsError($"{path}.count", "must be a whole number, at least 1"));
        }

        switch (String(segment["kind"]))
        {
            case "edge":
                if (String(segment["edge"]) is not { } edge || !s_edges.Contains(edge))
                {
                    errors.Add(new SettingsError($"{path}.edge", "must be top, bottom, left or right"));
                }

                if (!TryNumber(segment["from"], out var from) || !TryNumber(segment["to"], out var to))
                {
                    errors.Add(new SettingsError(path, "needs numeric from and to positions"));
                }
                else if (from == to && count > 1)
                {
                    errors.Add(new SettingsError($"{path}.to", "starts and ends at the same position"));
                }

                if (!TryNumber(segment["depth"], out var depth) || depth <= 0 || depth > 1)
                {
                    errors.Add(new SettingsError($"{path}.depth", "must be above 0 and at most 1"));
                }

                if (segment["blend"] is { } blend && (!TryNumber(blend, out var bl) || bl <= 0))
                {
                    errors.Add(new SettingsError($"{path}.blend", "must be greater than 0"));
                }

                break;

            case "fixed":
                if (segment["area"] is not JsonObject area || !AreaValues(area, out var a))
                {
                    errors.Add(new SettingsError($"{path}.area", "needs numeric hmin, hmax, vmin and vmax"));
                }
                else if (a.HMin > a.HMax || a.VMin > a.VMax)
                {
                    errors.Add(new SettingsError($"{path}.area", "has its minimum past its maximum"));
                }

                break;

            case "off":
                break;

            default:
                errors.Add(new SettingsError($"{path}.kind", "must be edge, fixed or off"));
                break;
        }

        return errors.Count == before ? count : 0;
    }

    private static void CompileSegment(JsonObject segment, List<JsonObject> rects)
    {
        var n = TryNumber(segment["count"], out var c) ? (int)Math.Max(0, Math.Floor(c)) : 0;
        switch (String(segment["kind"]))
        {
            case "edge":
                var from = Number(segment["from"]);
                var to = Number(segment["to"]);
                var depth = Number(segment["depth"]);
                var blend = TryNumber(segment["blend"], out var bl) ? bl : DefaultBlend;
                var step = (to - from) / n;
                var half = blend * Math.Abs(step) / 2;
                var edge = String(segment["edge"]);
                for (var k = 0; k < n; k++)
                {
                    var center = from + ((k + 0.5) * step);
                    var a0 = center - half;
                    var a1 = center + half;
                    var rect = edge switch
                    {
                        "top" => Rect(a0, a1, 0, depth),
                        "bottom" => Rect(a0, a1, 1 - depth, 1),
                        "left" => Rect(0, depth, a0, a1),
                        "right" => Rect(1 - depth, 1, a0, a1),
                        _ => null,
                    };
                    if (rect is not null)
                    {
                        rects.Add(rect);
                    }
                }

                break;

            case "fixed":
                AreaValues(segment["area"] as JsonObject ?? [], out var area);
                for (var k = 0; k < n; k++)
                {
                    rects.Add(Rect(area.HMin, area.HMax, area.VMin, area.VMax));
                }

                break;

            case "off":
                for (var k = 0; k < n; k++)
                {
                    rects.Add(DisabledRect());
                }

                break;
        }
    }

    private static JsonObject Rect(double hmin, double hmax, double vmin, double vmax) => new()
    {
        ["hmin"] = Coord(hmin),
        ["hmax"] = Coord(hmax),
        ["vmin"] = Coord(vmin),
        ["vmax"] = Coord(vmax),
        ["group"] = 0,
    };

    // The UI's DISABLED_RECT: a dark LED at a point in the middle, which the mapping skips.
    private static JsonObject DisabledRect() => new()
    {
        ["hmin"] = 0.5,
        ["hmax"] = 0.5,
        ["vmin"] = 0.5,
        ["vmax"] = 0.5,
        ["group"] = 0,
        ["disabled"] = true,
    };

    // compile.ts coord(): clamp to 0..1, then Math.round(x * 1e4) / 1e4 (half up, exactly as JavaScript rounds).
    private static double Coord(double x)
    {
        if (!double.IsFinite(x))
        {
            return 0;
        }

        var scaled = Math.Min(1, Math.Max(0, x)) * 1e4;
        var rounded = Math.Floor(scaled);
        if (scaled - rounded >= 0.5)
        {
            rounded += 1;
        }

        return rounded / 1e4;
    }

    private static List<(int Index, int Start, int Length)> Outputs(JsonObject controller)
    {
        var list = new List<(int, int, int)>();
        foreach (var output in (controller["outputs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (TryInt(output["index"], out var index) && TryInt(output["start"], out var start) && TryInt(output["length"], out var length))
            {
                list.Add((index, Math.Max(0, start), Math.Max(0, length)));
            }
        }

        return list;
    }

    private static bool AreaValues(JsonObject area, out (double HMin, double HMax, double VMin, double VMax) values)
    {
        var ok = TryNumber(area["hmin"], out var hmin) & TryNumber(area["hmax"], out var hmax) &
            TryNumber(area["vmin"], out var vmin) & TryNumber(area["vmax"], out var vmax);
        values = (hmin, hmax, vmin, vmax);
        return ok;
    }

    private static string? String(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static double Number(JsonNode? node) => TryNumber(node, out var d) ? d : double.NaN;

    // Numbers parsed from JSON and numbers created in code (JsonValue.Create(int)) alike.
    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        value = v.TryGetValue(out double d) ? d : double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture);
        return double.IsFinite(value);
    }

    private static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        if (!TryNumber(node, out var d) || d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue)
        {
            return false;
        }

        value = (int)d;
        return true;
    }
}
