using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Backbloom.Server.Rpc;

/// <summary>
/// Parses requests and routes them to the command classes by name (replacing HyperAPI's 26-branch if/else),
/// enforcing access rights in one place.
/// </summary>
public sealed partial class RpcDispatcher
{
    private readonly Dictionary<string, IRpcCommand> _commands;
    private readonly ILogger _logger;

    public RpcDispatcher(IEnumerable<IRpcCommand> commands, ILogger<RpcDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(logger);
        _commands = commands.ToDictionary(c => c.Name, StringComparer.Ordinal);
        _logger = logger;
    }

    public IReadOnlyCollection<string> Commands => _commands.Keys;

    /// <summary>Handles one request message (UTF-8 JSON); replies go to the session.</summary>
    public async ValueTask HandleAsync(RpcSession session, ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        JsonObject message;
        try
        {
            message = JsonNode.Parse(utf8.Span) as JsonObject ?? throw new JsonException("The message isn't a JSON object.");
        }
        catch (JsonException ex)
        {
            LogParseFailed(session.Peer, ex.Message);
            session.Send(RpcReplies.Error("Errors during message parsing, please consult the Backbloom log.", string.Empty, 0));
            return;
        }

        var tan = message["tan"] is JsonValue t && t.TryGetValue(out double d) ? (int)d : 0;
        if (message["command"] is not JsonValue c || !c.TryGetValue(out string? command) || string.IsNullOrEmpty(command))
        {
            session.Send(RpcReplies.Error("Errors during message validation, please consult the Backbloom log.", string.Empty, tan));
            return;
        }

        var subcommand = message["subcommand"] is JsonValue s && s.TryGetValue(out string? sub) ? sub.Trim() : null;
        var context = new RpcContext(session, message, command, subcommand, tan, cancellationToken);

        if (!_commands.TryGetValue(command, out var handler))
        {
            context.Fail("Command not implemented", command);
            return;
        }

        if ((handler.Access == RpcAccess.Authorized && !session.Authorized) || (handler.Access == RpcAccess.Admin && !session.Admin))
        {
            context.Fail("No Authorization", command);
            return;
        }

        try
        {
            await handler.ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogCommandFailed(ex, context.FullCommand, session.Peer);
            context.Fail($"Internal error: {ex.Message}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unreadable JSON-RPC message from {Peer}: {Reason}")]
    private partial void LogParseFailed(string peer, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "JSON-RPC command {Command} from {Peer} failed")]
    private partial void LogCommandFailed(Exception exception, string command, string peer);
}
