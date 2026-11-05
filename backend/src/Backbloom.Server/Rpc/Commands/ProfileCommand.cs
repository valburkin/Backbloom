using System.Text.Json.Nodes;
using Backbloom.Engine;
using Backbloom.Engine.Platform;

namespace Backbloom.Server.Rpc;

/// <summary>
/// <c>profile</c> (new in Backbloom): the layout's profiles (the UI's presets) at runtime. <c>getstate</c> replies
/// <c>{ profile, dismissed }</c> (see <see cref="Payloads.ProfileState"/>); <c>select</c> <c>{ id: string | null }</c>
/// turns the profile with that id on whatever programs run, until <c>dismiss</c>, another <c>select</c>, or
/// <c>select</c> with <c>id</c> null (or without it), which goes back to choosing by the running programs (the choice
/// is saved as <c>presets.selected</c>); <c>dismiss</c> turns the profile that is on off (see
/// <see cref="ProfileMonitor.Dismiss"/>; not saved). Both reply <c>{ profile, dismissed }</c>. <c>programs</c> (admins)
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
                context.Reply(Payloads.ProfileState(profiles.State));
                break;
            case "dismiss":
                context.Reply(Payloads.ProfileState(profiles.Dismiss()));
                break;
            case "select" when context.Message["id"] is { } id && (id is not JsonValue v || !v.TryGetValue(out string? _)):
                context.Fail("id must be a profile's id or null (choose by the running programs)");
                break;
            case "select":
                var wanted = context.GetString("id");
                if (profiles.Select(wanted))
                {
                    context.Reply(Payloads.ProfileState(profiles.State));
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
