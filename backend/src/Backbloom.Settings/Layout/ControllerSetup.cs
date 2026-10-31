using System.Text.Json.Nodes;

namespace Backbloom.Settings.Layout;

/// <summary>One strip on a controller: the output range it is wired to (in the controller's LED index space).</summary>
/// <param name="Brightness">Scale applied to the strip's LEDs (0..1, the strip's <c>brightness</c>; 1 when not set).</param>
public sealed record StripRange(string Id, string Name, int Start, int Length, double Brightness);

/// <summary>One LED controller as the engine drives it.</summary>
/// <param name="Id">The layout's controller id (legacy configurations: <see cref="ControllerSetup.LegacyId"/>).</param>
/// <param name="Device">The driver settings (the <c>device</c> section's shape: <c>type</c> + driver settings).</param>
/// <param name="Leds">One sampling area per LED of this controller, in the <c>leds</c> setting's shape.</param>
public sealed record ControllerConfig(string Id, string Name, bool Enabled, JsonObject Device, JsonArray Leds, IReadOnlyList<StripRange> Strips)
{
    public int LedCount => Leds.Count;
}

/// <summary>
/// What a zone plays on its own (the UI's <c>ZoneRole</c>): the effect <see cref="Effect"/> (by its name in the effect
/// catalog), or, without one, the color <see cref="R"/>, <see cref="G"/>, <see cref="B"/> (sRGB).
/// </summary>
public sealed record ZoneRole(string? Effect, byte R = 0, byte G = 0, byte B = 0)
{
    public bool IsEffect => Effect is not null;

    public static ZoneRole OfEffect(string name) => new(name);

    public static ZoneRole OfColor(byte r, byte g, byte b) => new(null, r, g, b);

    /// <summary>The role as the layout writes it: <c>{ kind: "effect", effect }</c> or <c>{ kind: "color", color: [r, g, b] }</c>.</summary>
    public JsonObject ToJson() => Effect is { } effect
        ? new JsonObject { ["kind"] = "effect", ["effect"] = effect }
        : new JsonObject { ["kind"] = "color", ["color"] = new JsonArray(R, G, B) };
}

/// <summary>
/// LEDs that play something of their own instead of what the lights show (the UI's <c>CompiledZone</c>): every LED of
/// every section with the same role, across strips and controllers. Their LEDs are disabled in the controllers' LEDs.
/// </summary>
/// <param name="Leds">Indices among every controller's LEDs (the pipeline's LEDs), in the order strips and LEDs come.</param>
/// <param name="Rects">Where each of those LEDs would sample the screen ('auto'): the layout an effect's picture is mapped with.</param>
public sealed record LayoutZone(ZoneRole Role, IReadOnlyList<int> Leds, JsonArray Rects);

/// <summary>A profile of the layout: other roles for the strips' sections while one of <see cref="Apps"/> runs.</summary>
/// <param name="Apps">Executable names such as "game.exe" (compared case-insensitively).</param>
/// <param name="Game">The programs are games: Game mode is on while the profile runs (<c>game: true</c>).</param>
public sealed record LayoutProfile(string Id, string Name, IReadOnlyList<string> Apps, bool Game = false);

/// <summary>
/// The controllers the one pipeline feeds, in order: each gets the slice of the pipeline's LEDs that follows the
/// previous controller's. Built from the <c>layout</c> setting when there is one, else from the legacy single
/// controller (<c>device</c> + <c>leds</c>, e.g. a HyperHDR import).
/// </summary>
public sealed record ControllerSetup(IReadOnlyList<ControllerConfig> Controllers, bool FromLayout)
{
    /// <summary>Id of the one controller of a configuration without a layout (the UI's id for it, too).</summary>
    public const string LegacyId = "controller-1";

    public int LedCount => Controllers.Sum(c => c.LedCount);

    /// <summary>The zones (sections that play an effect or a color of their own); none without a layout.</summary>
    public IReadOnlyList<LayoutZone> Zones { get; init; } = [];

    /// <summary>The profile this setup was compiled for, or null (the layout as it is saved).</summary>
    public string? ProfileId { get; init; }

    /// <summary>Every controller's LEDs, one after the other: the pipeline's <c>leds</c>.</summary>
    public JsonArray AllLeds()
    {
        var all = new JsonArray();
        foreach (var controller in Controllers)
        {
            foreach (var led in controller.Leds)
            {
                all.Add(led?.DeepClone());
            }
        }

        return all;
    }

    /// <summary>
    /// The setup the stored settings describe. <paramref name="layout"/> is the normalized <c>layout</c> section as the
    /// store keeps it (see <see cref="LayoutSpecification.Normalize"/>), compiled as the profile
    /// <paramref name="profileId"/> has it (null or an id the layout doesn't have: as saved); without a layout, the
    /// legacy controller is <paramref name="device"/> driving <paramref name="leds"/>.
    /// </summary>
    public static ControllerSetup FromSettings(JsonNode? layout, JsonNode? leds, JsonNode? device, string legacyName, string? profileId = null)
    {
        if (layout is JsonObject spec && spec["controllers"] is JsonArray { Count: > 0 })
        {
            return LayoutSpecification.Compile(spec, profileId);
        }

        var legacy = new ControllerConfig(
            LegacyId,
            legacyName,
            Enabled: true,
            device as JsonObject ?? [],
            leds as JsonArray ?? [],
            []);
        return new ControllerSetup([legacy], FromLayout: false);
    }
}
