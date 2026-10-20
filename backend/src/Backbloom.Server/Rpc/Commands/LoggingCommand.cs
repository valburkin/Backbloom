using System.Globalization;
using System.Text.Json.Nodes;
using Backbloom.Server.Logging;

namespace Backbloom.Server.Rpc;

/// <summary>
/// <c>logging</c>: <c>start</c> replies first, then pushes the recent history and every new line as
/// <c>{ command: "logging-update", result: { messages: [...] } }</c> (HyperHDR sent the history with the request's tan
/// before the reply); <c>stop</c> ends the stream.
/// </summary>
public sealed class LoggingCommand(LogBuffer logs) : IRpcCommand
{
    public const string StreamName = "logging";

    public string Name => "logging";

    public RpcAccess Access => RpcAccess.Admin;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var session = context.Session;
        switch (context.Subcommand)
        {
            case "start" when !session.IsHttp:
                context.Reply();
                if (!session.IsSubscribed(StreamName))
                {
                    session.Send(Update(logs.Recent()));
                    void Handler(LogEntry entry) => session.Send(Update([entry]));
                    logs.Added += Handler;
                    session.Subscribe(StreamName, new Subscription(() => logs.Added -= Handler));
                }

                break;

            case "stop":
                session.Unsubscribe(StreamName);
                context.Reply();
                break;

            default:
                context.Fail("Command not implemented");
                break;
        }

        return ValueTask.CompletedTask;
    }

    private static JsonObject Update(IEnumerable<LogEntry> entries)
    {
        var messages = new JsonArray();
        foreach (var e in entries)
        {
            messages.Add((JsonNode)new JsonObject
            {
                ["loggerName"] = e.LoggerName,
                ["levelString"] = e.LevelString,
                ["message"] = e.Message,
                ["utime"] = e.Time.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                ["fileName"] = string.Empty,
                ["line"] = "0",
                ["function"] = string.Empty,
            });
        }

        return new JsonObject { ["command"] = "logging-update", ["success"] = true, ["result"] = new JsonObject { ["messages"] = messages } };
    }
}
