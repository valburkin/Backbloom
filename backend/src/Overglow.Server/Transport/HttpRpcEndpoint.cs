using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Overglow.Server.Auth;
using Overglow.Server.Rpc;
using Overglow.Settings;

namespace Overglow.Server.Transport;

/// <summary>
/// <c>POST /json-rpc</c>: one command per request, answered with its reply. Sessions don't persist between requests;
/// remote clients authenticate with <c>Authorization: token &lt;API or user token&gt;</c>.
/// </summary>
public sealed class HttpRpcEndpoint(RpcDispatcher dispatcher, ISettingsStore settings, AuthService auth, ServerOptions options)
{
    public const string Path = "/json-rpc";

    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var connection = context.Connection;
        var isLocal = NetworkOrigin.IsLocal(connection.RemoteIpAddress, connection.LocalIpAddress);
        var (authorized, admin) = AccessPolicy.FromSettings(settings.GetSection("network")).InitialRights(isLocal);

        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("token ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header[6..].Trim();
            if (token.Length > AuthService.ApiTokenLength && auth.CheckUserToken(token))
            {
                authorized = admin = true;
            }
            else if (token.Length == AuthService.ApiTokenLength && auth.CheckApiToken(token))
            {
                authorized = true;
            }
        }

        if (context.Request.ContentLength > options.MaxMessageBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        var output = new FirstReplyOutput();
        using var session = new RpcSession(output, connection.RemoteIpAddress?.ToString() ?? "unknown", isLocal, authorized, admin, isHttp: true);
        await dispatcher.HandleAsync(session, body.GetBuffer().AsMemory(0, (int)body.Length), context.RequestAborted);

        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(output.Reply ?? RpcJson.ToUtf8(RpcReplies.Error("No reply", string.Empty, 0)), context.RequestAborted);
    }

    /// <summary>Keeps the first message (the reply); events can't be delivered over plain HTTP.</summary>
    private sealed class FirstReplyOutput : IRpcOutput
    {
        public byte[]? Reply { get; private set; }

        public int Pending => 0;

        public void Send(JsonObject message) => Reply ??= RpcJson.ToUtf8(message);

        public void SendRaw(byte[] utf8Json) => Reply ??= utf8Json;
    }
}
