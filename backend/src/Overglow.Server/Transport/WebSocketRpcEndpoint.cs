using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Overglow.Server.Auth;
using Overglow.Server.Rpc;
using Overglow.Settings;

namespace Overglow.Server.Transport;

/// <summary>
/// JSON-RPC over WebSocket on any path of the web port (the UI connects to <c>ws://host:port</c>). Requests of one
/// connection are handled in order; replies and events leave through one queue so they never interleave. When the
/// app shuts down, connections are closed at once (Kestrel would otherwise wait for them until its shutdown timeout).
/// </summary>
public sealed partial class WebSocketRpcEndpoint(
    RpcDispatcher dispatcher,
    ISettingsStore settings,
    AuthService auth,
    ServerOptions options,
    IHostApplicationLifetime lifetime,
    ILogger<WebSocketRpcEndpoint> logger)
{
    private readonly ILogger _logger = logger;

    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var connection = context.Connection;
        var isLocal = NetworkOrigin.IsLocal(connection.RemoteIpAddress, connection.LocalIpAddress);
        var peer = connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!isLocal && auth.HasDefaultPassword)
        {
            // As in HyperHDR: remote access stays closed until the factory password was changed.
            LogRemoteRefused(peer);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var (authorized, admin) = AccessPolicy.FromSettings(settings.GetSection("network")).InitialRights(isLocal);
        var output = new ChannelOutput();
        using var session = new RpcSession(output, peer, isLocal, authorized, admin);
        LogConnected(peer, isLocal, admin);

        var aborted = context.RequestAborted;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(aborted, lifetime.ApplicationStopping);
        var sender = SendLoopAsync(socket, output.Reader, aborted);
        try
        {
            await ReceiveLoopAsync(socket, session, stopping.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            LogConnectionLost(peer, ex.Message);
        }
        finally
        {
            session.Dispose();
            output.Complete();
            await sender;
        }

        LogDisconnected(peer);
    }

    private static async Task SendLoopAsync(WebSocket socket, ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(cancellationToken))
            {
                await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The client went away; nothing left to deliver.
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, RpcSession session, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        while (socket.State == WebSocketState.Open)
        {
            buffer.ResetWrittenCount();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.GetMemory(16 * 1024), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                buffer.Advance(result.Count);
                if (buffer.WrittenCount > options.MaxMessageBytes)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", cancellationToken);
                    return;
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                await dispatcher.HandleAsync(session, buffer.WrittenMemory, cancellationToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSocket client {Peer} connected (local: {IsLocal}, admin without login: {Admin})")]
    private partial void LogConnected(string peer, bool isLocal, bool admin);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSocket client {Peer} disconnected")]
    private partial void LogDisconnected(string peer);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WebSocket client {Peer} connection lost: {Reason}")]
    private partial void LogConnectionLost(string peer, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused remote client {Peer}: change the default password to allow remote access")]
    private partial void LogRemoteRefused(string peer);

    /// <summary>Unbounded queue of serialized messages, drained by the send loop.</summary>
    private sealed class ChannelOutput : IRpcOutput
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = false });

        public ChannelReader<byte[]> Reader => _channel.Reader;

        public int Pending => _channel.Reader.Count;

        public void Send(JsonObject message) => _channel.Writer.TryWrite(RpcJson.ToUtf8(message));

        public void SendRaw(byte[] utf8Json) => _channel.Writer.TryWrite(utf8Json);

        public void Complete() => _channel.Writer.TryComplete();
    }
}
