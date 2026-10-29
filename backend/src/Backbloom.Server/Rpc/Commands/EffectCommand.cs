using System.Text.Json.Nodes;
using Backbloom.Engine;

namespace Backbloom.Server.Rpc;

/// <summary>
/// <c>effect</c>: runs an effect at a priority, <c>{ priority: 1..253, effect: { name }, duration?: ms, origin?: name }</c>
/// (HyperHDR's HyperAPI::handleEffectCommand). An effect replaces what ran at its priority and ends when it is
/// cleared, replaced or its duration is over. Effect arguments, scripts and images aren't supported (HyperHDR
/// rejected arguments too). A music effect is refused while sound capture is off.
/// </summary>
public sealed class EffectCommand(LightingEngine engine) : IRpcCommand
{
    public string Name => "effect";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var priority = context.GetInt("priority");
        var origin = context.GetString("origin");
        var effect = context.Message["effect"] as JsonObject;
        var name = effect?["name"] is JsonValue v && v.TryGetValue(out string? n) ? n : null;
        if (priority is not (>= 1 and <= 253))
        {
            context.Fail("priority must be a number from 1 to 253");
        }
        else if (name is null)
        {
            context.Fail("effect.name is required");
        }
        else if (origin is { Length: < 4 or > 20 })
        {
            context.Fail("origin must have 4 to 20 characters");
        }
        else if (effect!["args"] is JsonObject { Count: > 0 } || context.Message.ContainsKey("pythonScript") || context.Message.ContainsKey("imageData"))
        {
            context.Fail("Effect arguments, scripts and images aren't supported");
        }
        else
        {
            switch (engine.RunEffect(name, priority.Value, context.GetInt("duration") ?? -1, $"{origin ?? "JsonRpc"}@{context.Session.Peer}"))
            {
                case EffectStart.Started:
                    context.Reply();
                    break;
                case EffectStart.NoSound:
                    context.Fail("Music needs sound input, which is off.");
                    break;
                default:
                    context.Fail($"Effect '{name}' not found");
                    break;
            }
        }

        return ValueTask.CompletedTask;
    }
}
