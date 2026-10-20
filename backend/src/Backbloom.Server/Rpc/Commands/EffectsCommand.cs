using System.Text.Json.Nodes;
using Backbloom.Core.Effects;
using Backbloom.Core.Layout;
using Backbloom.Engine;
using Backbloom.Settings;

namespace Backbloom.Server.Rpc;

/// <summary>
/// <c>effects</c> (new in Backbloom): <c>previews</c> returns a few looping seconds of an effect (<c>name</c>) or of
/// every effect on the user's LEDs, for the UI's effect cards: <c>{ fps, ledCount, standard, previews: [{ name,
/// frames }] }</c>, <c>frames</c> the RGB bytes of each frame in base64, LEDs in the order of <c>leds</c>. Without
/// enabled LEDs the previews use <see cref="EffectPreview.StandardFrame"/> (<c>standard</c> true). Rendered on simulated
/// time (music effects hear a built-in loop), so the lights and the sound capture aren't involved; kept until the LED
/// layout changes.
/// </summary>
public sealed class EffectsCommand(ISettingsStore settings) : IRpcCommand
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _frames = new(StringComparer.Ordinal);
    private string? _layoutKey;

    public string Name => "effects";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Subcommand != "previews")
        {
            context.Fail("unknown or missing subcommand");
            return ValueTask.CompletedTask;
        }

        var name = context.GetString("name");
        var definitions = name is null ? EffectEngine.Available : EffectEngine.Available.Where(e => e.Name == name).ToList();
        if (definitions.Count == 0)
        {
            context.Fail($"Effect '{name}' not found");
            return ValueTask.CompletedTask;
        }

        var leds = settings.GetSection("leds");
        var layout = LedLayout.FromJson(leds);
        var standard = !layout.Areas.Any(a => !a.Disabled);
        if (standard)
        {
            layout = EffectPreview.StandardFrame();
        }

        var previews = new JsonArray();
        lock (_lock)
        {
            var key = standard ? string.Empty : leds!.ToJsonString();
            if (key != _layoutKey)
            {
                _frames.Clear();
                _layoutKey = key;
            }

            foreach (var definition in definitions)
            {
                if (!_frames.TryGetValue(definition.Name, out var frames))
                {
                    frames = Convert.ToBase64String(EffectPreview.Render(definition, layout).Frames);
                    _frames[definition.Name] = frames;
                }

                previews.Add((JsonNode)new JsonObject { ["name"] = definition.Name, ["frames"] = frames });
            }
        }

        context.Reply(new JsonObject
        {
            ["fps"] = EffectPreview.DefaultFramesPerSecond,
            ["ledCount"] = layout.Count,
            ["standard"] = standard,
            ["previews"] = previews,
        });
        return ValueTask.CompletedTask;
    }
}
