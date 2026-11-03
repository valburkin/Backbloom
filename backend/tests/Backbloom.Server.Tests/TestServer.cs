using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Backbloom.App;
using Backbloom.Devices;
using Backbloom.Engine.Platform;
using Backbloom.Server.Auth;
using Backbloom.Settings;
using Backbloom.Tests.Platform;

namespace Backbloom.Server.Tests;

/// <summary>
/// The real host (same composition as the app) on a random loopback port with a temporary data directory. Requests
/// with a <see cref="PeerHeader"/> look to the server as if they came from that address (as Kestrel would report a
/// device on the network), so tests reach "remote" code paths without listening on the network.
/// </summary>
internal sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly bool _ownsDirectory;

    private TestServer(WebApplication app, string dataDirectory, Uri baseUri, bool ownsDirectory)
    {
        _app = app;
        DataDirectory = dataDirectory;
        BaseUri = baseUri;
        _ownsDirectory = ownsDirectory;
    }

    public string DataDirectory { get; }

    public Uri BaseUri { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>Test-only request header: the peer address the server sees (see <see cref="PeerFilter"/>).</summary>
    public const string PeerHeader = "X-Test-Peer";

    /// <summary>A device on the local network.</summary>
    public static readonly IPAddress NetworkPeer = IPAddress.Parse("192.168.1.50");

    /// <summary>A password for tests that sign in from another device.</summary>
    public const string Password = "test-password";

    public static string NewDataDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"backbloom-server-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The startup effect (on by default) is switched off unless a test's settings name it.</summary>
    public const string NoStartupEffect = """{ "enable": false, "type": "effect", "color": [255, 0, 0], "effect": "Rainbow swirl fast", "duration_ms": 3000 }""";

    /// <summary>
    /// Starts a server in a new data directory; <paramref name="settings"/> (sections as JSON) are stored first, with
    /// the startup effect off unless they contain <c>foregroundEffect</c>, and the first run completed unless
    /// <paramref name="firstRun"/> (so the LEDs run as before the first-run welcome existed); <paramref name="password"/>
    /// is set when given (other devices are refused without one).
    /// </summary>
    public static async Task<TestServer> StartAsync(string? settings = null, bool firstRun = false, Action<AppOptions, IServiceCollection>? configure = null, string? password = null)
    {
        var dir = NewDataDirectory();
        var sections = JsonNode.Parse(settings ?? "{}")!.AsObject();
        if (!sections.ContainsKey("foregroundEffect"))
        {
            sections["foregroundEffect"] = JsonNode.Parse(NoStartupEffect);
        }

        using (var services = SettingsServices.Open(new SettingsOptions(dir, null), Schema(), NullLoggerFactory.Instance))
        {
            var result = services.Store.Update(sections);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            if (!firstRun)
            {
                services.Setup.CompleteFirstRun();
            }

            if (password is not null)
            {
                Assert.True(new AuthService(services.Auth).SetPassword(password));
            }
        }

        return await StartInAsync(dir, ownsDirectory: true, configure: configure);
    }

    /// <summary>
    /// Starts a server on an existing data directory, which is kept unless <paramref name="ownsDirectory"/>. The
    /// process list is a fake without HyperHDR unless <paramref name="configure"/> registers another one.
    /// </summary>
    public static async Task<TestServer> StartInAsync(string dir, bool ownsDirectory = false, string[]? args = null, Action<AppOptions, IServiceCollection>? configure = null)
    {
        var app = BackbloomHost.Build(
            args ?? ["--data-dir", dir, "--legacy-data-dir", "none", "--bind", "127.0.0.1", "--port", "0", "--capture", "synthetic", "--fps", "60", "--ui-dir", Path.Combine(dir, "no-ui")],
            (options, services) =>
            {
                services.AddSingleton<IProcessList>(new FakeProcessList());

                // no test may scan or switch this PC's Wi-Fi
                services.AddSingleton<Backbloom.Devices.Setup.IWifi>(new Backbloom.Devices.Setup.NoWifi());
                services.AddSingleton<IStartupFilter, PeerFilter>();
                configure?.Invoke(options, services);
            });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestServer(app, dir, new Uri(address), ownsDirectory);
    }

    public static SettingsSchema Schema() =>
        SettingsSchema.CreateBuiltin(new DriverRegistry().RegisterAll().Drivers.ToDictionary(d => d.Type, d => d.Schema, StringComparer.Ordinal));

    public static void DeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // A device thread may still hold a file for a moment; the temp folder is cleaned up eventually.
        }
    }

    /// <summary>A WebSocket client from this computer, or from <paramref name="peer"/> (see <see cref="PeerHeader"/>).</summary>
    public async Task<RpcClient> ConnectAsync(IPAddress? peer = null, string? origin = null)
    {
        var socket = new ClientWebSocket();
        if (peer is not null)
        {
            socket.Options.SetRequestHeader(PeerHeader, peer.ToString());
        }

        if (origin is not null)
        {
            socket.Options.SetRequestHeader("Origin", origin);
        }

        await socket.ConnectAsync(new Uri($"ws://{BaseUri.Authority}"), TestContext.Current.CancellationToken);
        return new RpcClient(socket);
    }

    /// <summary>A WebSocket client on another device (<see cref="NetworkPeer"/>).</summary>
    public Task<RpcClient> ConnectFromNetworkAsync() => ConnectAsync(NetworkPeer);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (_ownsDirectory)
        {
            DeleteDirectory(DataDirectory);
        }
    }
}

/// <summary>Sets the connection's peer address from <see cref="TestServer.PeerHeader"/> before anything else runs.</summary>
internal sealed class PeerFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (IPAddress.TryParse(context.Request.Headers[TestServer.PeerHeader].ToString(), out var peer))
            {
                context.Connection.RemoteIpAddress = peer;
            }

            return nextMiddleware(context);
        });
        next(app);
    };
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
    public JsonObject NextEvent(string command, int timeoutMs = 5000) => NextEvent(command, static _ => true, timeoutMs);

    /// <summary>
    /// Takes the oldest unread event with this command name that matches, waiting for it if needed. Use it when the
    /// server may push unrelated events of the same kind first (e.g. a source timing out on its own).
    /// </summary>
    public JsonObject NextEvent(string command, Func<JsonObject, bool> match, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        lock (_events)
        {
            while (true)
            {
                var index = _events.FindIndex(e => e["command"]?.GetValue<string>() == command && match(e));
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
