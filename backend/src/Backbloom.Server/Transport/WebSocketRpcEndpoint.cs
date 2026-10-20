using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Backbloom.Server.Auth;
using Backbloom.Server.Rpc;

namespace Backbloom.Server.Transport;

/// <summary>
/// JSON-RPC over WebSocket on any path of the web port (the UI connects to <c>ws://host:port</c>). Requests of one
/// connection are handled in order; replies and events leave through one queue so they never interleave. When the
/// app shuts down, connections are closed at once (Kestrel would otherwise wait for them until its shutdown timeout).
/// This computer has full access; other devices sign in, and are refused while no password is set (see
/// <see cref="LocalAccess"/>).
/// </summary>
public sealed partial class WebSocketRpcEndpoint(
    RpcDispatcher dispatcher,
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
        var isLocal = LocalAccess.IsLoopback(connection.RemoteIpAddress);
        var peer = connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!isLocal && !auth.HasPassword)
        {
            // nobody could sign in: remote access stays closed until the user set a password
            LogRemoteRefused(peer);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var output = new ChannelOutput();
        using var session = new RpcSession(output, peer, isLocal, authorized: isLocal, admin: isLocal);
        LogConnected(peer, isLocal);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSocket client {Peer} connected (this computer: {IsLocal})")]
    private partial void LogConnected(string peer, bool isLocal);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSocket client {Peer} disconnected")]
    private partial void LogDisconnected(string peer);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WebSocket client {Peer} connection lost: {Reason}")]
    private partial void LogConnectionLost(string peer, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused remote client {Peer}: no password is set (set one to allow remote access)")]
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
