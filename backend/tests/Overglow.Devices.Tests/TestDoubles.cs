using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Devices.Transport;

namespace Overglow.Devices.Tests;

/// <summary>Captures datagrams instead of sending them.</summary>
internal sealed class RecordingChannel : IDatagramChannel
{
    public IPEndPoint? Remote { get; private set; }

    public ConcurrentQueue<byte[]> Packets { get; } = new();

    public bool Disposed { get; private set; }

    public void Connect(IPEndPoint remote) => Remote = remote;

    public bool Send(ReadOnlySpan<byte> datagram)
    {
        Packets.Enqueue(datagram.ToArray());
        return true;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Answers HTTP requests from a callback and records them.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Uri, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.ToString(), body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>A driver whose open can be made to fail, recording what the runner sends.</summary>
internal sealed class FakeDriver(RetryPolicy retryPolicy, int failOpens = 0) : ILedDriver
{
    private int _failOpens = failOpens;

    public string Type => "fake";

    public RetryPolicy RetryPolicy { get; } = retryPolicy;

    public string? Status => null;

    public int Opens { get; private set; }

    public int Closes { get; private set; }

    public bool Disposed { get; private set; }

    public ConcurrentQueue<byte[]> Frames { get; } = new();

    public ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        Opens++;
        if (_failOpens > 0)
        {
            _failOpens--;
            throw new IOException("controller unreachable");
        }

        return ValueTask.CompletedTask;
    }

    public bool Write(ReadOnlySpan<byte> colors)
    {
        Frames.Enqueue(colors.ToArray());
        return true;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        Closes++;
        return ValueTask.CompletedTask;
    }

    public void Dispose() => Disposed = true;
}

internal static class Wait
{
    public static void Until(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            Thread.Sleep(5);
        }
    }
}

internal static class Contexts
{
    public static DriverContext With(HttpMessageHandler handler, IDatagramChannel channel) =>
        new(NullLoggerFactory.Instance, new HttpClient(handler), () => channel, (_, _) => ValueTask.FromResult(IPAddress.Parse("192.0.2.10")));
}
