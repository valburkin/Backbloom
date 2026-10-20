using System.Text.Json.Nodes;
using Backbloom.Core.Color;
using Backbloom.Core.Priorities;
using Backbloom.Engine;

namespace Backbloom.Server.Rpc;

/// <summary>
/// <c>color</c>: shows a static color at a priority, <c>{ priority: 1..253, color: [r, g, b, ...], duration?: ms,
/// origin?: name }</c> (HyperHDR's HyperAPI::handleColorCommand; only the first color is used, as there).
/// </summary>
public sealed class ColorCommand(LightingEngine engine) : IRpcCommand
{
    public string Name => "color";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var priority = context.GetInt("priority");
        var origin = context.GetString("origin");
        if (priority is not (>= 1 and <= 253))
        {
            context.Fail("priority must be a number from 1 to 253");
        }
        else if (context.Message["color"] is not JsonArray { Count: >= 3 } color || color.Any(c => c is not JsonValue v || !v.TryGetValue(out double _)))
        {
            context.Fail("color must be an array of at least three numbers ([r, g, b, ...])");
        }
        else if (origin is { Length: < 4 or > 20 })
        {
            context.Fail("origin must have 4 to 20 characters");
        }
        else
        {
            var rgb = new Rgb8(Channel(color[0]), Channel(color[1]), Channel(color[2]));
            engine.SetColor(priority.Value, rgb, context.GetInt("duration") ?? -1, $"{origin ?? "JsonRpc"}@{context.Session.Peer}");
            context.Reply();
        }

        return ValueTask.CompletedTask;
    }

    private static byte Channel(JsonNode? node) => (byte)Math.Clamp((int)node!.GetValue<double>(), 0, 255);
}

/// <summary>
/// <c>clear</c>: removes the source at <c>priority</c> (1..253), or every color and effect with -1
/// (BaseAPI::clearPriority).
/// </summary>
public sealed class ClearCommand(LightingEngine engine) : IRpcCommand
{
    public string Name => "clear";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var priority = context.GetInt("priority");
        if (priority is null)
        {
            context.Fail("priority is required");
        }
        else if (priority < 0 || (priority > 0 && priority < Muxer.LowestEffectPriority))
        {
            engine.Clear(priority.Value);
            context.Reply();
        }
        else
        {
            context.Fail($"Priority {priority} is not allowed to be cleared");
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary><c>clearall</c>: removes every color and effect.</summary>
public sealed class ClearAllCommand(LightingEngine engine) : IRpcCommand
{
    public string Name => "clearall";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        engine.Clear(-1);
        context.Reply();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <c>sourceselect</c>: <c>{ priority }</c> shows that source regardless of the others, <c>{ auto: true }</c> returns
/// to automatic selection (the lowest priority number wins).
/// </summary>
public sealed class SourceSelectCommand(LightingEngine engine) : IRpcCommand
{
    public string Name => "sourceselect";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Message.ContainsKey("auto"))
        {
            // like HyperHDR: switching to manual without a selection keeps the automatic one
            engine.Sources.SetAutoSelect(context.GetBool("auto") ?? false);
            context.Reply();
        }
        else if (context.GetInt("priority") is { } priority)
        {
            if (engine.Sources.Select(priority))
            {
                context.Reply();
            }
            else
            {
                context.Fail($"Priority {priority} isn't a source");
            }
        }
        else
        {
            context.Fail("Priority request is invalid");
        }

        return ValueTask.CompletedTask;
    }
}
