using System.Text.Json.Nodes;
using Backbloom.Engine;
using Backbloom.Engine.Platform;

namespace Backbloom.Server.Rpc;

/// <summary>
/// <c>profile</c> (new in Backbloom): the layout's profiles at runtime. <c>getstate</c> replies <c>{ profile }</c>
/// (see <see cref="Payloads.Profile()"/>); <c>select</c> <c>{ id: string | null }</c> runs the profile with that id
/// whatever programs run (for trying a profile out), until it is called with <c>id</c> null (or without it), which goes
/// back to choosing by the running programs; it replies <c>{ profile }</c>, nothing is saved. <c>programs</c> (admins)
/// lists the programs with a window for the program picker: <c>{ programs: [{ exe, title }] }</c>, one per executable,
/// sorted by it.
/// </summary>
public sealed class ProfileCommand(ProfileMonitor profiles, IProgramList programs) : IRpcCommand
{
    public string Name => "profile";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (context.Subcommand)
        {
            case "getstate":
                context.Reply(new JsonObject { ["profile"] = Payloads.Profile(profiles.Current) });
                break;
            case "select" when context.Message["id"] is { } id && (id is not JsonValue v || !v.TryGetValue(out string? _)):
                context.Fail("id must be a profile's id or null (choose by the running programs)");
                break;
            case "select":
                var wanted = context.GetString("id");
                if (profiles.Select(wanted))
                {
                    context.Reply(new JsonObject { ["profile"] = Payloads.Profile(profiles.Current) });
                }
                else
                {
                    context.Fail($"The layout has no profile '{wanted}'");
                }

                break;
            case "programs" when !context.Session.Admin:
                context.Fail("No Authorization");
                break;
            case "programs":
                var list = new JsonArray();
                foreach (var program in programs.WindowedPrograms())
                {
                    list.Add((JsonNode)new JsonObject { ["exe"] = program.Executable, ["title"] = program.Title });
                }

                context.Reply(new JsonObject { ["programs"] = list });
                break;
            default:
                context.Fail("unknown or missing subcommand");
                break;
        }

        return ValueTask.CompletedTask;
    }
}
