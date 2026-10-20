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
/// The controllers the one pipeline feeds, in order: each gets the slice of the pipeline's LEDs that follows the
/// previous controller's. Built from the <c>layout</c> setting when there is one, else from the legacy single
/// controller (<c>device</c> + <c>leds</c>, e.g. a HyperHDR import).
/// </summary>
public sealed record ControllerSetup(IReadOnlyList<ControllerConfig> Controllers, bool FromLayout)
{
    /// <summary>Id of the one controller of a configuration without a layout (the UI's id for it, too).</summary>
    public const string LegacyId = "controller-1";

    public int LedCount => Controllers.Sum(c => c.LedCount);

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
    /// store keeps it (see <see cref="LayoutSpecification.Normalize"/>); without one, the legacy controller is
    /// <paramref name="device"/> driving <paramref name="leds"/>.
    /// </summary>
    public static ControllerSetup FromSettings(JsonNode? layout, JsonNode? leds, JsonNode? device, string legacyName)
    {
        if (layout is JsonObject spec && spec["controllers"] is JsonArray { Count: > 0 })
        {
            return LayoutSpecification.Compile(spec);
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
