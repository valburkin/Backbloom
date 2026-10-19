using System.Text.Json.Nodes;
using Overglow.Engine;
using Overglow.Settings;

namespace Overglow.Server.Rpc;

/// <summary>
/// <c>config</c>: <c>getschema</c>, <c>getconfig</c> and <c>setconfig</c>. setconfig replaces the top-level sections
/// it receives and keeps the others; invalid values reject the whole update with per-field errors (HyperHDR silently
/// reset them to defaults), and saving also works while the lights are off.
/// </summary>
public sealed class ConfigCommand(ISettingsStore settings, SettingsSchema schema) : IRpcCommand
{
    public string Name => "config";

    public RpcAccess Access => RpcAccess.Authorized;

    public static string Describe(IReadOnlyList<SettingsError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return "Invalid settings: " + string.Join("; ", errors.Select(e => e.ToString()));
    }

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (context.Subcommand)
        {
            case "getschema":
                context.Reply(schema.ToClientSchema());
                break;

            case "getconfig" when !context.Session.Admin:
            case "setconfig" when !context.Session.Admin:
                context.Fail("No Authorization", "config");
                break;

            case "getconfig":
                context.Reply(settings.Snapshot());
                break;

            case "setconfig":
                if (context.Message["config"] is not JsonObject config)
                {
                    context.Fail("The config parameter must be an object");
                    break;
                }

                var result = settings.Update(config);
                if (result.Success)
                {
                    context.Reply();
                }
                else
                {
                    context.Fail(Describe(result.Errors), errors: result.Errors);
                }

                break;

            default:
                context.Fail("unknown or missing subcommand");
                break;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <c>adjustment</c>: changes the color adjustment immediately and stores it in <c>color.channelAdjustment</c>, so the
/// <c>settings-update</c> it causes is true (HyperHDR applied it only until restart). Needs admin rights for that reason.
/// </summary>
public sealed class AdjustmentCommand(ISettingsStore settings) : IRpcCommand
{
    public string Name => "adjustment";

    public RpcAccess Access => RpcAccess.Admin;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Message["adjustment"] is not JsonObject adjustment)
        {
            context.Fail("The adjustment parameter must be an object", "adjustment");
            return ValueTask.CompletedTask;
        }

        var color = settings.GetSection("color") as JsonObject ?? [];
        var list = color["channelAdjustment"] as JsonArray;
        var current = list is [JsonObject first, ..] ? first : [];
        foreach (var (name, value) in adjustment)
        {
            current[name] = value?.DeepClone();
        }

        if (list is null || list.Count == 0)
        {
            color["channelAdjustment"] = new JsonArray(current.DeepClone());
        }
        else
        {
            list[0] = current.DeepClone();
        }

        var result = settings.Update(new JsonObject { ["color"] = color });
        if (result.Success)
        {
            context.Reply(command: "adjustment");
        }
        else
        {
            context.Fail(ConfigCommand.Describe(result.Errors), "adjustment", result.Errors);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary><c>componentstate</c>: switches ALL, SYSTEMGRABBER or LEDDEVICE.</summary>
public sealed class ComponentStateCommand(LightingEngine engine) : IRpcCommand
{
    public string Name => "componentstate";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = context.Message["componentstate"] as JsonObject;
        var id = state?["component"] is JsonValue c && c.TryGetValue(out string? s) ? s : null;
        if (!Components.TryParse(id, out var component))
        {
            context.Fail(id is null ? "invalid component name" : $"Component {id} isn't available in this version", "componentstate");
            return ValueTask.CompletedTask;
        }

        var on = state?["state"] is not JsonValue v || !v.TryGetValue(out bool b) || b;
        engine.Components.Set(component, on);
        context.Reply(command: "componentstate");
        return ValueTask.CompletedTask;
    }
}
