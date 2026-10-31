using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backbloom.Settings.Layout;

/// <summary>
/// The <c>layout</c> setting (the UI's <c>LayoutSpec</c>, <c>ui/src/layout/types.ts</c>): a schematic room with the
/// screen on its front wall, controllers with their outputs and driver settings, and the strips wired to those
/// outputs, each a path on the room's walls, ceiling or floor. The path's bend points split a strip into sections;
/// each section repeats a stretch of the screen's outline (by default the stretch it faces, seen straight on against
/// the front wall), or shows the whole picture, one area of it, nothing, an effect or a color of its own (a zone), or is
/// a gap without LEDs. Profiles give the sections other roles while a program runs. This class validates the layout,
/// fills in each controller's <c>device</c>, applies a profile and compiles the result into one sampling area per LED
/// per controller and the zones. The math mirrors <c>ui/src/layout/room.ts</c>, <c>compile.ts</c> and
/// <c>profiles.ts</c> step by step (the shared fixture <c>tests/fixtures/layouts/ui-multi-controller.*.json</c> keeps
/// them equal).
/// </summary>
/// <remarks>
/// Rules (the errors of <c>ui/src/layout/validate.ts</c>; its warnings aren't enforced): a room 0.5 to 50 m in each
/// direction, a screen of positive size on the front wall, furniture desk, tv or none; at least one controller;
/// controller ids unique; each controller's outputs have unique indexes, a length of at least 1 and tile its LED index
/// space from 0 without gaps or overlaps, so its LED count is the sum of its outputs' lengths (at most
/// <see cref="MaxLedsPerController"/>); a strip has a whole number of LEDs (at least 1), a path of two or more points
/// on the room's surfaces with a length with LEDs above 0 (gaps don't count) and matched LED positions (<c>at</c>)
/// inside the strip and in order, valid sections (<c>next</c>: edge with numeric <c>from</c>/<c>to</c>, whole, area
/// with an area that isn't inside out, none, effect with a name, color with three whole numbers 0..255, or gap), valid
/// <c>dark</c>, <c>depth</c>, <c>blend</c> and <c>brightness</c>, names an existing controller and output, one strip
/// per output, and has at most as many LEDs as its output (fewer: the rest stays dark). <c>profiles</c>, when given,
/// is a list of profiles with unique non-empty ids, a name, <c>apps</c> as a list of program names, an optional
/// <c>game</c> flag (true or false: Game mode while the profile runs) and
/// <c>sections</c> as an object of lists per strip id whose entries are null or valid sections other than gap (a list
/// for a strip that isn't there, or of another length, is only a warning in the UI and is ignored when applied).
/// Properties the backend doesn't use (the UI's <c>kind</c>, <c>host</c>, <c>pin</c>, <c>name</c>, ...) are kept as
/// they are.
/// </remarks>
public static class LayoutSpecification
{
    public const int MaxLedsPerController = 10_000;

    // compile.ts
    private const double DefaultBlend = 1.2;
    private const double DefaultDepth = 0.1;
    private const double MinHalf = 0.01;
    private const double MaxHalf = 0.5;

    // validate.ts
    private const double RoomMin = 0.5;
    private const double RoomMax = 50;
    private const double SurfaceTolerance = 1e-3;

    private static readonly string[] s_surfaces = ["front", "left", "right", "back", "ceiling", "floor"];
    private static readonly string[] s_furniture = ["desk", "tv", "none"];

    private enum Surface
    {
        Front,
        Left,
        Right,
        Back,
        Ceiling,
        Floor,
    }

    private enum Edge
    {
        Top,
        Right,
        Bottom,
        Left,
    }

    /// <summary>What a section does (<c>Section</c> in types.ts); <c>Auto</c>: no <c>next</c> given.</summary>
    private enum SectionKind
    {
        Auto,
        Edge,
        Whole,
        Area,
        None,
        Effect,
        Color,
        Gap,
        Unknown,
    }

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

        // validateLayout: the strips' paths are checked against the room only when the room is valid
        var room = ValidateRoom(spec["room"], $"{Root}.room", errors);

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
                ValidateStrip(strips[i], $"{Root}.strips.{i}", room, outputsByController, stripIds, usedOutputs, errors);
            }
        }

        if (spec.TryGetPropertyValue("profiles", out var profiles))
        {
            ValidateProfiles(profiles, $"{Root}.profiles", errors);
        }

        return errors.Count == before ? spec : null;
    }

    /// <summary>
    /// Compiles a layout that <see cref="Normalize"/> accepted (<c>compileLayout</c> and <c>compileZones</c>), as the
    /// profile <paramref name="profileId"/> has it (<see cref="ApplyProfile"/>; null or an unknown id: the layout itself).
    /// </summary>
    public static ControllerSetup Compile(JsonObject spec, string? profileId = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var applied = ApplyProfile(spec, profileId);
        var room = ParseRoom(applied["room"] as JsonObject ?? []);
        var strips = (applied["strips"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var result = new List<ControllerConfig>();
        var zones = new List<ZoneBuilder>();
        var zoneByRole = new Dictionary<ZoneRole, ZoneBuilder>();
        var offset = 0;
        foreach (var controller in (applied["controllers"] as JsonArray ?? []).OfType<JsonObject>())
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
                var rects = CompileStrip(strip, room, zone: false);
                var n = Math.Min(rects.Count, output.Length);
                for (var i = 0; i < n; i++)
                {
                    leds[output.Start + i] = rects[i];
                }

                AddZoneLeds(strip, room, offset + output.Start, output.Length, zones, zoneByRole);
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
            offset += size;
        }

        return new ControllerSetup(result, FromLayout: true)
        {
            Zones = [.. zones.Select(z => new LayoutZone(z.Role, z.Leds, z.Rects))],
            ProfileId = ReferenceEquals(applied, spec) ? null : profileId,
        };
    }

    /// <summary>The profiles of a layout in list order (each with an id; programs that aren't names are left out).</summary>
    public static IReadOnlyList<LayoutProfile> Profiles(JsonObject? spec)
    {
        var list = new List<LayoutProfile>();
        foreach (var profile in (spec?["profiles"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (String(profile["id"]) is { Length: > 0 } id)
            {
                var apps = (profile["apps"] as JsonArray ?? []).Select(String).OfType<string>().Where(a => a.Trim().Length > 0).ToList();
                var game = profile["game"] is JsonValue g && g.TryGetValue(out bool isGame) && isGame;
                list.Add(new LayoutProfile(id, String(profile["name"]) ?? id, apps, game));
            }
        }

        return list;
    }

    /// <summary>
    /// The layout as the profile <paramref name="profileId"/> has it (<c>applyProfile</c> / <c>stripInProfile</c>):
    /// each strip the profile lists with as many entries as the strip has sections gets the profile's sections (null:
    /// auto); gaps stay, and a profile entry can't make one. Returns <paramref name="spec"/> itself for null or an
    /// unknown id, else a changed copy.
    /// </summary>
    public static JsonObject ApplyProfile(JsonObject spec, string? profileId)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var profile = profileId is null
            ? null
            : (spec["profiles"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(p => String(p["id"]) == profileId);
        if (profile is null)
        {
            return spec;
        }

        var copy = spec.DeepClone().AsObject();
        var sections = profile["sections"] as JsonObject;
        foreach (var strip in (copy["strips"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (String(strip["id"]) is not { } id || sections?[id] is not JsonArray list || strip["path"] is not JsonArray path ||
                list.Count != path.Count - 1)
            {
                continue;
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (path[i] is not JsonObject point || KindOf(point["next"]) == "gap" || KindOf(list[i]) == "gap")
                {
                    continue;
                }

                if (list[i] is { } section)
                {
                    point["next"] = section.DeepClone();
                }
                else
                {
                    point.Remove("next");
                }
            }
        }

        return copy;

        static string? KindOf(JsonNode? section) => section is JsonObject o ? String(o["kind"]) : null;
    }

    /// <summary>
    /// Rects of one strip's LEDs in wiring order (<c>compileStrip</c>), in the room <paramref name="room"/> (the
    /// layout's <c>room</c>). Each LED takes what its section does: on an <c>edge</c> section the LEDs spread evenly
    /// over its stretch of the outline (by where they are in the section), each <c>depth</c> screen heights into the
    /// picture and as wide along the edge as the LED spacing there times <c>blend</c>; <c>whole</c> and <c>area</c>
    /// LEDs show that area; <c>none</c> LEDs, dark LEDs and LEDs of a zone (<c>effect</c>, <c>color</c>) get the
    /// disabled rect. With <paramref name="zone"/>, LEDs of a zone get the rect 'auto' would give them instead and every
    /// other LED the disabled rect (the layout a zone's effect is mapped with).
    /// </summary>
    public static List<JsonObject> CompileStrip(JsonObject strip, JsonObject room, bool zone = false)
    {
        ArgumentNullException.ThrowIfNull(strip);
        ArgumentNullException.ThrowIfNull(room);
        return CompileStrip(strip, ParseRoom(room), zone);
    }

    private static List<JsonObject> CompileStrip(JsonObject strip, Room room, bool zone)
    {
        var n = StripLedCount(strip);
        var rects = new List<JsonObject>(n);
        if (n == 0)
        {
            return rects;
        }

        var path = ParsePath(strip);
        var leds = PlaceLeds(room, path, n);
        if (leds.Count != n)
        {
            for (var k = 0; k < n; k++)
            {
                rects.Add(DisabledRect());
            }

            return rects;
        }

        var dark = DarkMask(strip, n);
        var depth = TryNumber(strip["depth"], out var dp) ? dp : DefaultDepth;
        var blend = TryNumber(strip["blend"], out var bl) ? bl : DefaultBlend;
        var side = Math.Min(1, depth * room.ScreenHeight / room.ScreenWidth);
        var perPiece = new int[path.Count];
        foreach (var led in leds)
        {
            perPiece[led.Piece]++;
        }

        for (var k = 0; k < n; k++)
        {
            var led = leds[k];
            if (dark[k])
            {
                rects.Add(DisabledRect());
                continue;
            }

            var section = SectionAt(room, path, led.Piece);
            if (section.IsZone != zone)
            {
                rects.Add(DisabledRect());
                continue;
            }

            if (zone)
            {
                section = AutoSection(room, path[led.Piece], path[Math.Min(path.Count - 1, led.Piece + 1)]);
            }

            switch (section.Kind)
            {
                case SectionKind.Edge:
                    var p = section.From + (led.T * (section.To - section.From));
                    var spacing = Math.Abs(section.To - section.From) / perPiece[led.Piece];
                    var half = Math.Min(MaxHalf, Math.Max(MinHalf, blend * spacing / 2));
                    rects.Add(EdgeRect(p, half, depth, side));
                    break;
                case SectionKind.Whole:
                    rects.Add(Rect(0, 1, 0, 1));
                    break;
                case SectionKind.Area:
                    rects.Add(Rect(section.HMin, section.HMax, section.VMin, section.VMax));
                    break;
                default:
                    rects.Add(DisabledRect());
                    break;
            }
        }

        return rects;
    }

    // compileZones, for one strip: every LED of a zone section (not dark, within its output) joins the zone of its role,
    // with its index among all controllers' LEDs and its zone rect.
    private static void AddZoneLeds(JsonObject strip, Room room, int start, int outputLength, List<ZoneBuilder> zones, Dictionary<ZoneRole, ZoneBuilder> byRole)
    {
        var n = StripLedCount(strip);
        if (n == 0)
        {
            return;
        }

        var path = ParsePath(strip);
        var leds = PlaceLeds(room, path, n);
        if (leds.Count != n)
        {
            return;
        }

        List<JsonObject>? rects = null;
        var dark = DarkMask(strip, n);
        for (var k = 0; k < n; k++)
        {
            var section = SectionAt(room, path, leds[k].Piece);
            if (!section.IsZone || dark[k] || k >= outputLength)
            {
                continue;
            }

            rects ??= CompileStrip(strip, room, zone: true);
            var role = section.Role();
            if (!byRole.TryGetValue(role, out var builder))
            {
                builder = new ZoneBuilder(role);
                byRole[role] = builder;
                zones.Add(builder);
            }

            builder.Leds.Add(start + k);
            builder.Rects.Add((JsonNode)rects[k]);
        }
    }

    // ---- validation ----

    // validateRoom: the room, or null when anything about it is wrong.
    private static Room? ValidateRoom(JsonNode? node, string path, List<SettingsError> errors)
    {
        if (node is not JsonObject room)
        {
            errors.Add(new SettingsError(path, "must be an object (the room: width, depth, height, screen, furniture)"));
            return null;
        }

        var before = errors.Count;
        var sized = true;
        double Size(string name)
        {
            if (!TryNumber(room[name], out var v) || v < RoomMin || v > RoomMax)
            {
                errors.Add(new SettingsError($"{path}.{name}", Invariant($"must be from {RoomMin} to {RoomMax} (meters)")));
                sized = false;
            }

            return v;
        }

        var width = Size("width");
        var depth = Size("depth");
        var height = Size("height");

        double sx = 0, sy = 0, sw = 0, sh = 0;
        if (room["screen"] is not JsonObject screen || !TryNumber(screen["x"], out sx) || !TryNumber(screen["y"], out sy) ||
            !TryNumber(screen["width"], out sw) || !TryNumber(screen["height"], out sh) || sw <= 0 || sh <= 0)
        {
            errors.Add(new SettingsError($"{path}.screen", "needs numeric x, y, width and height, with a size above 0"));
        }
        else if (sized && (sx < -SurfaceTolerance || sy < -SurfaceTolerance || sx + sw > width + SurfaceTolerance || sy + sh > height + SurfaceTolerance))
        {
            errors.Add(new SettingsError($"{path}.screen", Invariant($"reaches past the front wall ({width} x {height} m)")));
        }

        if (String(room["furniture"]) is not { } furniture || !s_furniture.Contains(furniture))
        {
            errors.Add(new SettingsError($"{path}.furniture", "must be desk, tv or none"));
        }

        return errors.Count == before ? new Room(width, depth, height, sx, sy, sw, sh) : null;
    }

    private static void ValidateStrip(
        JsonNode? node,
        string path,
        Room? room,
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

        var count = ValidateStripShape(strip, path, room, errors);

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
            return;
        }

        if (count > length)
        {
            errors.Add(new SettingsError($"{path}.ledCount", $"the strip has {count} LEDs, but its output drives only {length}"));
        }
    }

    // validateStripShape (errors only). Returns the strip's LED count (stripLedCount; 0 when invalid).
    private static int ValidateStripShape(JsonObject strip, string path, Room? room, List<SettingsError> errors)
    {
        var countIsInt = TryInt(strip["ledCount"], out var count);
        if (!countIsInt || count < 1)
        {
            errors.Add(new SettingsError($"{path}.ledCount", "must be a whole number, at least 1"));
        }

        if (strip["path"] is not JsonArray { Count: >= 2 } points)
        {
            errors.Add(new SettingsError($"{path}.path", "needs at least two points (a start and an end)"));
        }
        else
        {
            var pointsOk = true;
            var parsed = new List<PathPoint>(points.Count);
            for (var i = 0; i < points.Count; i++)
            {
                var p = $"{path}.path.{i}";
                if (points[i] is not JsonObject point || !TryPoint(point, out var pathPoint))
                {
                    errors.Add(new SettingsError(p, "must be on a surface (front, left, right, back, ceiling or floor) with numeric x and y"));
                    pointsOk = false;
                    continue;
                }

                if (room is { } r)
                {
                    var (w, h) = SurfaceSize(r, pathPoint.Surface);
                    if (pathPoint.X < -SurfaceTolerance || pathPoint.Y < -SurfaceTolerance || pathPoint.X > w + SurfaceTolerance || pathPoint.Y > h + SurfaceTolerance)
                    {
                        errors.Add(new SettingsError(p, Invariant($"is outside the room (the {s_surfaces[(int)pathPoint.Surface]} is {w} x {h} m)")));
                        pointsOk = false;
                    }
                }

                if (point.TryGetPropertyValue("at", out var at) && (!TryNumber(at, out var a) || a < 0 || (countIsInt && a > count)))
                {
                    errors.Add(new SettingsError($"{p}.at", "must be an LED position from 0 to the strip's LED count"));
                    pointsOk = false;
                }

                if (point.TryGetPropertyValue("next", out var next) && i < points.Count - 1)
                {
                    ValidateSection(next, $"{p}.next", errors);
                }

                parsed.Add(pathPoint);
            }

            if (pointsOk && room is { } rm && countIsInt && count >= 1)
            {
                if (!(LitLength(rm, parsed) > 0))
                {
                    errors.Add(new SettingsError($"{path}.path", "has no length with LEDs on it: it starts and ends at the same spot, or is all gaps"));
                }
                else
                {
                    var stations = PathStations(rm, parsed, count);
                    for (var i = 1; i < stations.Length; i++)
                    {
                        if (stations[i] < stations[i - 1])
                        {
                            errors.Add(new SettingsError($"{path}.path.{i}", "goes backwards: its LED position comes before the previous point's"));
                            break;
                        }
                    }
                }
            }
        }

        if (strip.TryGetPropertyValue("dark", out var darkNode))
        {
            if (darkNode is not JsonArray dark)
            {
                errors.Add(new SettingsError($"{path}.dark", "must be a list"));
            }
            else
            {
                for (var i = 0; i < dark.Count; i++)
                {
                    if (dark[i] is not JsonObject range || !TryInt(range["start"], out var start) || !TryInt(range["count"], out var n) ||
                        start < 0 || n < 1 || (countIsInt && (long)start + n > count))
                    {
                        errors.Add(new SettingsError($"{path}.dark.{i}", "must be a range of the strip's LEDs (whole numbers: start at least 0, count at least 1)"));
                    }
                }
            }
        }

        if (strip.TryGetPropertyValue("depth", out var depth) && (!TryNumber(depth, out var dp) || dp <= 0 || dp > 1))
        {
            errors.Add(new SettingsError($"{path}.depth", "must be above 0 and at most 1"));
        }

        if (strip.TryGetPropertyValue("blend", out var blend) && (!TryNumber(blend, out var bl) || bl <= 0))
        {
            errors.Add(new SettingsError($"{path}.blend", "must be greater than 0"));
        }

        if (strip.TryGetPropertyValue("brightness", out var brightness) && (!TryNumber(brightness, out var b) || b < 0 || b > 1))
        {
            errors.Add(new SettingsError($"{path}.brightness", "must be a number from 0 to 1"));
        }

        return StripLedCount(strip);
    }

    // validateSection (errors only): a known kind; edge with numeric from and to; area with an area that isn't
    // inside out (one reaching outside the picture is only a warning in the UI: it is cut to fit); effect with a name;
    // color with three whole numbers 0..255.
    private static void ValidateSection(JsonNode? node, string path, List<SettingsError> errors)
    {
        // ParseSection leaves the numbers it can't read NaN; a `next` given as null is an error too (the UI checks every
        // `next` that isn't undefined)
        var section = node is null ? Section.Invalid : ParseSection(node);
        switch (section.Kind)
        {
            case SectionKind.Unknown:
                errors.Add(new SettingsError(path, "must be a section with the kind edge, whole, area, none, effect, color or gap"));
                break;
            case SectionKind.Edge when double.IsNaN(section.From) || double.IsNaN(section.To):
                errors.Add(new SettingsError(path, "needs numeric from and to (positions on the screen's outline, 0..4)"));
                break;
            case SectionKind.Area when double.IsNaN(section.HMin) || double.IsNaN(section.HMax) || double.IsNaN(section.VMin) || double.IsNaN(section.VMax):
                errors.Add(new SettingsError($"{path}.area", "needs numeric hmin, hmax, vmin and vmax"));
                break;
            case SectionKind.Area when section.HMin > section.HMax || section.VMin > section.VMax:
                errors.Add(new SettingsError($"{path}.area", "has its minimum past its maximum"));
                break;
            case SectionKind.Effect when string.IsNullOrWhiteSpace(section.Effect):
                errors.Add(new SettingsError($"{path}.effect", "needs the name of the effect it plays"));
                break;
            case SectionKind.Color when section.Color < 0:
                errors.Add(new SettingsError($"{path}.color", "needs a color: three whole numbers from 0 to 255"));
                break;
            default:
                break;
        }
    }

    // validateProfiles (errors only; a profile's list for a strip that isn't there or that doesn't fit its sections is a
    // warning in the UI, and such a list is ignored when the profile is applied).
    private static void ValidateProfiles(JsonNode? node, string path, List<SettingsError> errors)
    {
        if (node is not JsonArray profiles)
        {
            errors.Add(new SettingsError(path, "must be a list"));
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < profiles.Count; i++)
        {
            var p = $"{path}.{i}";
            if (profiles[i] is not JsonObject profile || String(profile["id"]) is not { } id || id.Trim().Length == 0)
            {
                errors.Add(new SettingsError($"{p}.id", "needs an id (a non-empty text)"));
                continue;
            }

            if (!ids.Add(id))
            {
                errors.Add(new SettingsError($"{p}.id", $"\"{id}\" is used by another profile"));
            }

            if (String(profile["name"]) is not { } name || name.Trim().Length == 0)
            {
                errors.Add(new SettingsError($"{p}.name", "needs a name (a non-empty text)"));
            }

            if (profile["apps"] is not JsonArray apps || apps.Any(a => String(a) is not { } app || app.Trim().Length == 0))
            {
                errors.Add(new SettingsError($"{p}.apps", "must be a list of program names (such as \"game.exe\")"));
            }

            if (profile.TryGetPropertyValue("game", out var game) && (game is not JsonValue gv || !gv.TryGetValue(out bool _)))
            {
                errors.Add(new SettingsError($"{p}.game", "must be true or false"));
            }

            if (profile["sections"] is not JsonObject sections)
            {
                errors.Add(new SettingsError($"{p}.sections", "must be an object with a list of sections per strip id"));
                continue;
            }

            foreach (var (stripId, value) in sections)
            {
                var sp = $"{p}.sections.{stripId}";
                if (value is not JsonArray list)
                {
                    errors.Add(new SettingsError(sp, "must be a list (a section per part of the strip, null for auto)"));
                    continue;
                }

                for (var j = 0; j < list.Count; j++)
                {
                    if (list[j] is null)
                    {
                        continue;
                    }

                    if (list[j] is JsonObject o && String(o["kind"]) == "gap")
                    {
                        errors.Add(new SettingsError($"{sp}.{j}", "can't be a gap: gaps belong to the strip"));
                    }
                    else
                    {
                        ValidateSection(list[j], $"{sp}.{j}", errors);
                    }
                }
            }
        }
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

    // ---- room geometry (room.ts) ----

    private static (double W, double H) SurfaceSize(Room room, Surface surface) => surface switch
    {
        Surface.Front or Surface.Back => (room.Width, room.Height),
        Surface.Left or Surface.Right => (room.Depth, room.Height),
        _ => (room.Width, room.Depth),
    };

    // toWorld: x from the left wall, y from the front wall, z up from the floor.
    private static Vec3 ToWorld(Room room, PathPoint p) => p.Surface switch
    {
        Surface.Front => new Vec3(p.X, 0, room.Height - p.Y),
        Surface.Left => new Vec3(0, room.Depth - p.X, room.Height - p.Y),
        Surface.Right => new Vec3(room.Width, p.X, room.Height - p.Y),
        Surface.Back => new Vec3(room.Width - p.X, room.Depth, room.Height - p.Y),
        Surface.Ceiling => new Vec3(p.X, room.Depth - p.Y, room.Height),
        _ => new Vec3(p.X, p.Y, 0),
    };

    private static double Distance(Vec3 a, Vec3 b) => Hypot(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    // Math.hypot as V8 computes it (scaled by the largest value, Kahan-compensated sum), so path lengths match the UI's
    // to the last bit.
    private static double Hypot(double x, double y, double z)
    {
        x = Math.Abs(x);
        y = Math.Abs(y);
        z = Math.Abs(z);
        var max = Math.Max(x, Math.Max(y, z));
        if (double.IsPositiveInfinity(max))
        {
            return double.PositiveInfinity;
        }

        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z))
        {
            return double.NaN;
        }

        if (max == 0)
        {
            return 0;
        }

        var sum = 0.0;
        var compensation = 0.0;
        foreach (var value in (ReadOnlySpan<double>)[x, y, z])
        {
            var n = value / max;
            var summand = (n * n) - compensation;
            var preliminary = sum + summand;
            compensation = preliminary - sum - summand;
            sum = preliminary;
        }

        return Math.Sqrt(sum) * max;
    }

    // isGap: the section from point i to the next has no LEDs.
    private static bool IsGap(List<PathPoint> path, int i) => path[i].Next.Kind == SectionKind.Gap;

    // litLength: the length of the parts of a path that have LEDs (gaps left out).
    private static double LitLength(Room room, List<PathPoint> path)
    {
        var total = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            if (!IsGap(path, i - 1))
            {
                total += Distance(ToWorld(room, path[i - 1]), ToWorld(room, path[i]));
            }
        }

        return total;
    }

    // pathStations: each point's position along the strip in LEDs; matched points keep theirs, the first and last
    // default to 0 and the LED count, the rest are spaced by length between their known neighbors. Gaps count no
    // length, so both ends of a gap are at the same position.
    private static double[] PathStations(Room room, List<PathPoint> path, int ledCount)
    {
        var n = path.Count;
        if (n == 0)
        {
            return [];
        }

        var cumulative = new double[n];
        for (var i = 1; i < n; i++)
        {
            cumulative[i] = cumulative[i - 1] + (IsGap(path, i - 1) ? 0 : Distance(ToWorld(room, path[i - 1]), ToWorld(room, path[i])));
        }

        var known = path.Select(p => p.At).ToArray();
        known[0] ??= 0;
        if (n > 1)
        {
            known[n - 1] ??= ledCount;
        }

        var stations = known.Select(k => k ?? double.NaN).ToArray();
        var prev = 0;
        for (var i = 1; i < n; i++)
        {
            if (known[i] is not { } here)
            {
                continue;
            }

            var from = known[prev]!.Value;
            var span = cumulative[i] - cumulative[prev];
            for (var j = prev + 1; j < i; j++)
            {
                var f = span > 0 ? (cumulative[j] - cumulative[prev]) / span : (double)(j - prev) / (i - prev);
                stations[j] = from + (f * (here - from));
            }

            prev = i;
        }

        return stations;
    }

    // placeLeds: LED k sits at station k + 0.5, between the stations of the points around it (never in a gap); LEDs
    // before the first point or past the last stay at that point. Returns the path piece each LED is on (between
    // point `Piece` and the next) and where in it (0..1).
    private static List<PlacedLed> PlaceLeds(Room room, List<PathPoint> path, int count)
    {
        var placed = new List<PlacedLed>(count);
        if (path.Count == 0 || count == 0)
        {
            return placed;
        }

        if (path.Count == 1)
        {
            placed.AddRange(Enumerable.Repeat(new PlacedLed(0, 0), count));
            return placed;
        }

        var stations = PathStations(room, path, count);
        var piece = 0;
        for (var k = 0; k < count; k++)
        {
            var s = k + 0.5;

            // move on past pieces that end before s, and past empty ones (gaps) that end at it
            while (piece < path.Count - 2 && (s > stations[piece + 1] || (s == stations[piece + 1] && stations[piece + 1] == stations[piece])))
            {
                piece++;
            }

            var s0 = stations[piece];
            var s1 = stations[piece + 1];
            var t = s1 != s0 ? Math.Min(1, Math.Max(0, (s - s0) / (s1 - s0))) : 0;
            placed.Add(new PlacedLed(piece, t));
        }

        return placed;
    }

    // ---- the screen's outline (room.ts) ----

    // onFrontWall: where a point is on the front wall when the room is looked at straight on (depth dropped).
    private static (double X, double Y) OnFrontWall(Room room, PathPoint p)
    {
        var v = ToWorld(room, p);
        return (v.X, room.Height - v.Z);
    }

    // nearestOutline: the outline position (0..4 clockwise from the screen's top-left corner, one unit per edge)
    // closest to a front-wall position.
    private static double NearestOutline(Room room, double x, double y)
    {
        var (sx, sy, w, h) = (room.ScreenX, room.ScreenY, room.ScreenWidth, room.ScreenHeight);
        var px = Math.Min(sx + w, Math.Max(sx, x));
        var py = Math.Min(sy + h, Math.Max(sy, y));
        if (px == x && py == y)
        {
            // inside the screen: out to the closest edge (the first of equally close ones)
            ReadOnlySpan<double> d = [y - sy, sx + w - x, sy + h - y, x - sx];
            var i = 0;
            for (var j = 1; j < d.Length; j++)
            {
                if (d[j] < d[i])
                {
                    i = j;
                }
            }

            switch (i)
            {
                case 0:
                    py = sy;
                    break;
                case 1:
                    px = sx + w;
                    break;
                case 2:
                    py = sy + h;
                    break;
                default:
                    px = sx;
                    break;
            }
        }

        // on the border now; a corner counts to the edge that starts there, going clockwise
        if (py == sy && px < sx + w)
        {
            return (px - sx) / w;
        }

        if (px == sx + w && py < sy + h)
        {
            return 1 + ((py - sy) / h);
        }

        if (py == sy + h && px > sx)
        {
            return 2 + ((sx + w - px) / w);
        }

        return 3 + ((sy + h - py) / h);
    }

    // shortWay: `to` moved by whole turns so it is as close to `from` as possible.
    private static double ShortWay(double from, double to) => to + (4 * JsRound((from - to) / 4));

    // autoSection: the stretch of the outline a section faces, its two ends seen straight on against the front wall,
    // each at the closest point of the screen's outline.
    private static Section AutoSection(Room room, PathPoint a, PathPoint b)
    {
        var pa = OnFrontWall(room, a);
        var pb = OnFrontWall(room, b);
        var from = NearestOutline(room, pa.X, pa.Y);
        return Section.OfEdge(from, ShortWay(from, NearestOutline(room, pb.X, pb.Y)));
    }

    // sectionAt: what the section from point i to the next does, with auto worked out.
    private static Section SectionAt(Room room, List<PathPoint> path, int i)
    {
        var own = path[i].Next;
        return own.Kind != SectionKind.Auto ? own : AutoSection(room, path[i], path[Math.Min(path.Count - 1, i + 1)]);
    }

    // outlineEdge: the edge and the position along it (0..1, left to right or top to bottom) of an outline position.
    private static (Edge Edge, double Pos) OutlineEdge(double p)
    {
        var q = ((p % 4) + 4) % 4;
        if (q < 1)
        {
            return (Edge.Top, q);
        }

        if (q < 2)
        {
            return (Edge.Right, q - 1);
        }

        return q < 3 ? (Edge.Bottom, 1 - (q - 2)) : (Edge.Left, 1 - (q - 3));
    }

    // Math.round: the nearest integer, halves up (toward +infinity).
    private static double JsRound(double x)
    {
        var floor = Math.Floor(x);
        return x - floor >= 0.5 ? floor + 1 : floor;
    }

    // ---- rects (compile.ts) ----

    // edgeRect: depth is in screen heights; on the left and right edges it reaches `side` of the width.
    private static JsonObject EdgeRect(double p, double half, double depth, double side)
    {
        var (edge, pos) = OutlineEdge(p);
        var a0 = pos - half;
        var a1 = pos + half;
        return edge switch
        {
            Edge.Top => Rect(a0, a1, 0, depth),
            Edge.Bottom => Rect(a0, a1, 1 - depth, 1),
            Edge.Left => Rect(0, side, a0, a1),
            _ => Rect(1 - side, 1, a0, a1),
        };
    }

    // stripLedCount: invalid counts give none, fractions are floored.
    private static int StripLedCount(JsonObject strip) =>
        TryNumber(strip["ledCount"], out var n) ? (int)Math.Clamp(Math.Floor(n), 0, int.MaxValue) : 0;

    // darkMask
    private static bool[] DarkMask(JsonObject strip, int n)
    {
        var mask = new bool[n];
        foreach (var range in (strip["dark"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var start = (int)Math.Clamp(Math.Floor(Number(range["start"])), 0, int.MaxValue);
            var count = (int)Math.Clamp(Math.Floor(Number(range["count"])), 0, int.MaxValue);
            var end = (int)Math.Min(n, (long)start + count);
            for (var i = start; i < end; i++)
            {
                mask[i] = true;
            }
        }

        return mask;
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

    // ---- parsing ----

    // A validated room (Normalize accepted it); missing values read as 0.
    private static Room ParseRoom(JsonObject room)
    {
        var screen = room["screen"] as JsonObject ?? [];
        return new Room(
            Value(room["width"]),
            Value(room["depth"]),
            Value(room["height"]),
            Value(screen["x"]),
            Value(screen["y"]),
            Value(screen["width"]),
            Value(screen["height"]));

        static double Value(JsonNode? node) => TryNumber(node, out var v) ? v : 0;
    }

    private static List<PathPoint> ParsePath(JsonObject strip)
    {
        var path = new List<PathPoint>();
        foreach (var point in (strip["path"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (TryPoint(point, out var p))
            {
                path.Add(p);
            }
        }

        return path;
    }

    // A path point: a known surface and numeric x and y; `at` only when it is a finite number.
    private static bool TryPoint(JsonObject point, out PathPoint value)
    {
        value = default;
        var surface = Array.IndexOf(s_surfaces, String(point["surface"]));
        if (surface < 0 || !TryNumber(point["x"], out var x) || !TryNumber(point["y"], out var y))
        {
            return false;
        }

        value = new PathPoint((Surface)surface, x, y, TryNumber(point["at"], out var at) ? at : null, ParseSection(point["next"]));
        return true;
    }

    // A point's `next`: none (or null, which the UI treats the same: `if (own)`) is auto; numbers that can't be read
    // are NaN (validation reports them).
    private static Section ParseSection(JsonNode? node)
    {
        if (node is null)
        {
            return default;
        }

        if (node is not JsonObject section)
        {
            return Section.Invalid;
        }

        switch (String(section["kind"]))
        {
            case "edge":
                return Section.OfEdge(Number(section["from"]), Number(section["to"]));
            case "whole":
                return new Section(SectionKind.Whole, 0, 0, 0, 0, 0, 0);
            case "area":
                var area = section["area"] as JsonObject ?? [];
                return new Section(SectionKind.Area, 0, 0, Number(area["hmin"]), Number(area["hmax"]), Number(area["vmin"]), Number(area["vmax"]));
            case "none":
                return new Section(SectionKind.None, 0, 0, 0, 0, 0, 0);
            case "effect":
                return new Section(SectionKind.Effect, 0, 0, 0, 0, 0, 0) { Effect = String(section["effect"]) };
            case "color":
                return new Section(SectionKind.Color, 0, 0, 0, 0, 0, 0) { Color = ParseColor(section["color"]) };
            case "gap":
                return new Section(SectionKind.Gap, 0, 0, 0, 0, 0, 0);
            default:
                return Section.Invalid;
        }
    }

    // A color section's [r, g, b] (whole numbers 0..255) as 0xRRGGBB, or -1.
    private static int ParseColor(JsonNode? node)
    {
        if (node is not JsonArray { Count: 3 } rgb)
        {
            return -1;
        }

        var packed = 0;
        foreach (var channel in rgb)
        {
            if (!TryInt(channel, out var c) || c is < 0 or > 255)
            {
                return -1;
            }

            packed = (packed << 8) | c;
        }

        return packed;
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

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

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

    /// <summary>The schematic room (<c>Room</c> in types.ts), in meters.</summary>
    private readonly record struct Room(
        double Width,
        double Depth,
        double Height,
        double ScreenX,
        double ScreenY,
        double ScreenWidth,
        double ScreenHeight);

    /// <summary>
    /// A bend point of a strip's path, in meters on its surface; <c>At</c> is its matched LED position, <c>Next</c> the
    /// section from it to the next point.
    /// </summary>
    private readonly record struct PathPoint(Surface Surface, double X, double Y, double? At, Section Next);

    /// <summary>
    /// A section (<c>Section</c> in types.ts): <c>From</c>/<c>To</c> are the outline positions of an edge section
    /// (0..4 clockwise from the screen's top-left corner), the four bounds the area of an area section.
    /// </summary>
    /// <remarks><c>Effect</c>: the name an effect section plays; <c>Color</c>: a color section's 0xRRGGBB (-1: not a valid color).</remarks>
    private readonly record struct Section(SectionKind Kind, double From, double To, double HMin, double HMax, double VMin, double VMax)
    {
        public static Section Invalid { get; } = new(SectionKind.Unknown, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

        public string? Effect { get; init; }

        public int Color { get; init; } = -1;

        /// <summary>The LEDs play something of their own (<c>isZoneSection</c>).</summary>
        public bool IsZone => Kind is SectionKind.Effect or SectionKind.Color;

        public static Section OfEdge(double from, double to) => new(SectionKind.Edge, from, to, 0, 0, 0, 0);

        public ZoneRole Role() => Kind == SectionKind.Effect
            ? ZoneRole.OfEffect(Effect ?? string.Empty)
            : ZoneRole.OfColor((byte)(Color >> 16), (byte)(Color >> 8), (byte)Color);
    }

    /// <summary>A zone being compiled: its LEDs (indices among all controllers' LEDs) and their rects.</summary>
    private sealed class ZoneBuilder(ZoneRole role)
    {
        public ZoneRole Role { get; } = role;

        public List<int> Leds { get; } = [];

        public JsonArray Rects { get; } = [];
    }

    /// <summary>One LED placed on a strip's path: the piece it is on (from point <c>Piece</c> to the next) and where in it (0..1).</summary>
    private readonly record struct PlacedLed(int Piece, double T);

    /// <summary>A position in the room: x from the left wall, y from the front wall, z up from the floor (meters).</summary>
    private readonly record struct Vec3(double X, double Y, double Z);
}
