using System.Net;
using Overglow.Devices.Transport;

namespace Overglow.Devices.Drivers;

/// <summary>
/// Base of the drivers that stream to one host over UDP (port of <c>ProviderUdp</c>,
/// sources/led-drivers/net/ProviderUdp.cpp): resolves the host when the device opens and sends over a connected
/// socket.
/// </summary>
public abstract class UdpLedDriver : ILedDriver
{
    private readonly string _host;
    private readonly int _port;
    private readonly Func<string, CancellationToken, ValueTask<IPAddress>> _resolve;

    protected UdpLedDriver(string host, int port, DriverContext context, string what)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException($"No {what} target host is configured.", nameof(host));
        }

        if (port is <= 0 or > 65535)
        {
            throw new ArgumentException($"Invalid {what} port {port}.", nameof(port));
        }

        _host = host.Trim();
        _port = port;
        _resolve = context.ResolveHost;
        Channel = context.CreateDatagramChannel();
    }

    public abstract string Type { get; }

    public virtual RetryPolicy RetryPolicy => RetryPolicy.Default;

    public string? Status => null;

    protected IDatagramChannel Channel { get; }

    protected bool Connected { get; private set; }

    public virtual async ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        var address = await _resolve(_host, cancellationToken);
        Channel.Connect(new IPEndPoint(address, _port));
        Connected = true;
    }

    public abstract bool Write(ReadOnlySpan<byte> colors);

    public virtual ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        Connected = false;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Channel.Dispose();
        }
    }
}
