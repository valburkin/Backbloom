using System.Text.Json.Nodes;
using Backbloom.Settings;

namespace Backbloom.Server.Rpc;

/// <summary>Reply shapes of the HyperHDR JSON-RPC protocol.</summary>
public static class RpcReplies
{
    /// <summary><c>{ command, tan, success: true, info? }</c></summary>
    public static JsonObject Success(string command, int tan, JsonNode? info = null)
    {
        var reply = new JsonObject { ["command"] = command, ["tan"] = tan, ["success"] = true };
        if (info is not null)
        {
            reply["info"] = info;
        }

        return reply;
    }

    /// <summary>
    /// <c>{ command, tan, success: false, error }</c>, plus <c>errors: [{ path, message }]</c> when settings were
    /// rejected (an addition to the HyperHDR protocol; older clients just show <c>error</c>).
    /// </summary>
    public static JsonObject Error(string error, string command, int tan, IReadOnlyList<SettingsError>? errors = null)
    {
        var reply = new JsonObject { ["command"] = command, ["tan"] = tan, ["success"] = false, ["error"] = error };
        if (errors is { Count: > 0 })
        {
            var list = new JsonArray();
            foreach (var e in errors)
            {
                list.Add((JsonNode)new JsonObject { ["path"] = e.Path, ["message"] = e.Message });
            }

            reply["errors"] = list;
        }

        return reply;
    }

    /// <summary>A pushed event: <c>{ command: "&lt;name&gt;-update", data }</c>.</summary>
    public static JsonObject Event(string command, JsonNode? data) => new() { ["command"] = command, ["data"] = data };
}

/// <summary>One request being handled.</summary>
public sealed class RpcContext
{
    public RpcContext(RpcSession session, JsonObject message, string command, string? subcommand, int tan, CancellationToken cancellationToken)
    {
        Session = session;
        Message = message;
        Command = command;
        Subcommand = subcommand;
        Tan = tan;
        CancellationToken = cancellationToken;
    }

    public RpcSession Session { get; }

    /// <summary>The whole request object (parameters are top-level properties).</summary>
    public JsonObject Message { get; }

    public string Command { get; }

    public string? Subcommand { get; }

    public int Tan { get; }

    public CancellationToken CancellationToken { get; }

    /// <summary>"command-subcommand", the name replies carry.</summary>
    public string FullCommand => string.IsNullOrEmpty(Subcommand) ? Command : $"{Command}-{Subcommand}";

    public void Reply(JsonNode? info = null, string? command = null) =>
        Session.Send(RpcReplies.Success(command ?? FullCommand, Tan, info));

    public void Fail(string error, string? command = null, IReadOnlyList<SettingsError>? errors = null) =>
        Session.Send(RpcReplies.Error(error, command ?? FullCommand, Tan, errors));

    public string? GetString(string name) => Message[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public int? GetInt(string name) => Message[name] is JsonValue v && v.TryGetValue(out double d) ? (int)d : null;

    public bool? GetBool(string name) => Message[name] is JsonValue v && v.TryGetValue(out bool b) ? b : null;
}

/// <summary>Who may run a command.</summary>
public enum RpcAccess
{
    /// <summary>Anyone, before authorization (only <c>authorize</c>).</summary>
    Anyone,

    /// <summary>Authorized sessions (API access).</summary>
    Authorized,

    /// <summary>Admin sessions (configuration).</summary>
    Admin,
}

/// <summary>Handles one top-level JSON-RPC command (all of its subcommands).</summary>
public interface IRpcCommand
{
    string Name { get; }

    RpcAccess Access { get; }

    ValueTask ExecuteAsync(RpcContext context);
}
