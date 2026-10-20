using System.Text.Json.Nodes;

namespace Backbloom.Server.Rpc;

/// <summary>Where a session's replies and events go (a WebSocket, or the single response of an HTTP request).</summary>
public interface IRpcOutput
{
    /// <summary>Queues one message; must not block and may be called from any thread.</summary>
    void Send(JsonObject message);

    /// <summary>Queues one message that is already serialized (UTF-8 JSON).</summary>
    void SendRaw(byte[] utf8Json);

    /// <summary>Messages queued but not sent yet (lets streams skip a frame for slow clients).</summary>
    int Pending { get; }
}

/// <summary>
/// Per-connection state: rights, subscriptions and streams. Mirrors one HyperHDR <c>HyperAPI</c> object, but the
/// command logic lives in the stateless <see cref="IRpcCommand"/> classes.
/// </summary>
public sealed class RpcSession : IDisposable
{
    private readonly IRpcOutput _output;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, IDisposable> _subscriptions = new(StringComparer.Ordinal);
    private bool _disposed;

    public RpcSession(IRpcOutput output, string peer, bool isLocal, bool authorized, bool admin, bool isHttp = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        Peer = peer;
        IsLocal = isLocal;
        Authorized = authorized;
        Admin = admin;
        IsHttp = isHttp;
        InitialAuthorized = authorized;
        InitialAdmin = admin;
    }

    /// <summary>Client address, used in log messages and as the "origin" suffix.</summary>
    public string Peer { get; }

    /// <summary>A connection from this computer (loopback, see <see cref="Auth.LocalAccess"/>): full access without a password.</summary>
    public bool IsLocal { get; }

    /// <summary>One-shot HTTP request: no subscriptions or streams.</summary>
    public bool IsHttp { get; }

    /// <summary>May use the API (HyperHDR "authorized").</summary>
    public bool Authorized { get; set; }

    /// <summary>May read and change the configuration (HyperHDR "admin authorized").</summary>
    public bool Admin { get; set; }

    /// <summary>Rights the connection had before any login, restored by logout.</summary>
    public bool InitialAuthorized { get; }

    public bool InitialAdmin { get; }

    public bool IsDisposed
    {
        get
        {
            lock (_lock)
            {
                return _disposed;
            }
        }
    }

    public void Send(JsonObject message)
    {
        if (!IsDisposed)
        {
            _output.Send(message);
        }
    }

    /// <summary>Sends a pre-serialized message unless the client is still busy with earlier ones.</summary>
    public void SendRaw(byte[] utf8Json, int maxPending = 4)
    {
        if (!IsDisposed && _output.Pending <= maxPending)
        {
            _output.SendRaw(utf8Json);
        }
    }

    public bool IsSubscribed(string name)
    {
        lock (_lock)
        {
            return _subscriptions.ContainsKey(name);
        }
    }

    /// <summary>Registers a subscription (an event handler or stream); replaces an existing one with the same name.</summary>
    public void Subscribe(string name, IDisposable subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        IDisposable? previous;
        lock (_lock)
        {
            if (_disposed)
            {
                subscription.Dispose();
                return;
            }

            _subscriptions.Remove(name, out previous);
            _subscriptions[name] = subscription;
        }

        previous?.Dispose();
    }

    public void Unsubscribe(string name)
    {
        IDisposable? subscription;
        lock (_lock)
        {
            _subscriptions.Remove(name, out subscription);
        }

        subscription?.Dispose();
    }

    /// <summary>Drops every subscription (logout, or the connection closed).</summary>
    public void UnsubscribeAll()
    {
        List<IDisposable> all;
        lock (_lock)
        {
            all = [.. _subscriptions.Values];
            _subscriptions.Clear();
        }

        foreach (var s in all)
        {
            s.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        UnsubscribeAll();
    }
}

/// <summary>An <see cref="IDisposable"/> that runs an action once.</summary>
public sealed class Subscription(Action unsubscribe) : IDisposable
{
    private Action? _unsubscribe = unsubscribe;

    public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
}
