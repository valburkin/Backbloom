using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Overglow.App;

namespace Overglow.Server.Tests;

/// <summary>The real host (same composition as the app) on a random loopback port with a temporary data directory.</summary>
internal sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestServer(WebApplication app, string dataDirectory, Uri baseUri)
    {
        _app = app;
        DataDirectory = dataDirectory;
        BaseUri = baseUri;
    }

    public string DataDirectory { get; }

    public Uri BaseUri { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>Starts a server; <paramref name="settings"/> (a settings.json "settings" object) seeds the data directory.</summary>
    public static async Task<TestServer> StartAsync(string? settings = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"overglow-server-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        if (settings is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), $$"""{ "version": 1, "settings": {{settings}} }""");
        }

        var app = OverglowHost.Build(["--data-dir", dir, "--port", "0", "--fps", "60", "--ui-dir", Path.Combine(dir, "no-ui")]);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestServer(app, dir, new Uri(address));
    }

    public async Task<RpcClient> ConnectAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://{BaseUri.Authority}"), TestContext.Current.CancellationToken);
        return new RpcClient(socket);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A device thread may still hold a file for a moment; the temp folder is cleaned up eventually.
        }
    }
}

/// <summary>A minimal client of the JSON-RPC protocol: replies are matched by tan, everything else is an event.</summary>
internal sealed class RpcClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly List<JsonObject> _events = [];
    private readonly Task _receiver;
    private int _tan;

    public RpcClient(ClientWebSocket socket)
    {
        _socket = socket;
        _receiver = ReceiveAsync();
    }

    public async Task<JsonObject> CallAsync(string command, string? subcommand = null, JsonObject? parameters = null)
    {
        var tan = Interlocked.Increment(ref _tan);
        var message = parameters?.DeepClone().AsObject() ?? [];
        message["command"] = command;
        message["tan"] = tan;
        if (subcommand is not null)
        {
            message["subcommand"] = subcommand;
        }

        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[tan] = reply;
        await SendRawAsync(message.ToJsonString());
        return await reply.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    public Task SendRawAsync(string text) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

    /// <summary>Takes the oldest unread event with this command name, waiting for it if needed.</summary>
    public JsonObject NextEvent(string command, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        lock (_events)
        {
            while (true)
            {
                var index = _events.FindIndex(e => e["command"]?.GetValue<string>() == command);
                if (index >= 0)
                {
                    var e = _events[index];
                    _events.RemoveAt(index);
                    return e;
                }

                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"No {command} event.");
                }

                Monitor.Wait(_events, left);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }

        await _receiver;
        _socket.Dispose();
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[1 << 20];
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var count = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
                    count += result.Count;
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                var message = JsonNode.Parse(buffer.AsSpan(0, count))!.AsObject();
                var isEvent = message["command"]?.GetValue<string>().EndsWith("-update", StringComparison.Ordinal) == true &&
                    (message.ContainsKey("data") || message.ContainsKey("result"));
                if (!isEvent && message["tan"] is JsonValue t && _pending.TryRemove(t.GetValue<int>(), out var waiter))
                {
                    waiter.SetResult(message);
                }
                else
                {
                    lock (_events)
                    {
                        _events.Add(message);
                        Monitor.PulseAll(_events);
                    }
                }
            }
        }
        catch (WebSocketException)
        {
        }
    }
}
