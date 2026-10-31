using System.Text.Json.Nodes;
using Backbloom.Engine;

namespace Backbloom.Server.Rpc;

/// <summary>
/// <c>mode</c> (new in Backbloom): Ambient and Game mode. <c>getstate</c> replies <c>{ mode }</c> (see
/// <see cref="Payloads.Mode()"/>); <c>set</c> <c>{ mode: "ambient" | "game" }</c> saves the chosen mode
/// (<c>modes.mode</c>; Ambient while a game's profile has Game mode on also turns it off until another profile runs,
/// see <see cref="ModeMonitor.Set"/>) and replies <c>{ mode }</c>. API access is enough, like the Home controls.
/// </summary>
public sealed class ModeCommand(ModeMonitor modes) : IRpcCommand
{
    public string Name => "mode";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (context.Subcommand)
        {
            case "getstate":
                context.Reply(new JsonObject { ["mode"] = Payloads.Mode(modes.Current) });
                break;
            case "set":
                LightMode? wanted = context.GetString("mode") switch
                {
                    "ambient" => LightMode.Ambient,
                    "game" => LightMode.Game,
                    _ => null,
                };
                if (wanted is not { } mode)
                {
                    context.Fail("mode must be \"ambient\" or \"game\"");
                    break;
                }

                var result = modes.Set(mode);
                if (result.Success)
                {
                    context.Reply(new JsonObject { ["mode"] = Payloads.Mode(modes.Current) });
                }
                else
                {
                    context.Fail(ConfigCommand.Describe(result.Errors), errors: result.Errors);
                }

                break;
            default:
                context.Fail("unknown or missing subcommand");
                break;
        }

        return ValueTask.CompletedTask;
    }
}
