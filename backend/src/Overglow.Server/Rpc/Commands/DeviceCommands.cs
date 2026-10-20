using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Overglow.Devices;
using Overglow.Engine;

namespace Overglow.Server.Rpc;

/// <summary>
/// <c>leddevice</c>: identify (blink one LED with <c>ledDeviceType: "blink"</c>, or a driver's own identify, e.g. a
/// Hue light's alert), discover (<c>{ ledDeviceType, devices: [{ value, name, ... }] }</c>) and getProperties
/// (<c>{ properties }</c>). The driver tools only query the network (mDNS, SSDP, broadcasts) or talk to the device the
/// request names.
/// </summary>
public sealed class LedDeviceCommand(LightingEngine engine, DeviceTools tools) : IRpcCommand
{
    public string Name => "leddevice";

    public RpcAccess Access => RpcAccess.Authorized;

    public async ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var type = context.GetString("ledDeviceType")?.Trim() ?? string.Empty;
        var parameters = context.Message["params"] as JsonObject;
        try
        {
            switch (context.Subcommand)
            {
                case "identify" when type == "blink":
                    if (parameters?["blinkIndex"] is JsonValue v && v.TryGetValue(out double index))
                    {
                        engine.IdentifyLed((int)index);
                    }

                    context.Reply();
                    break;

                case "identify":
                    await tools.IdentifyAsync(type, parameters, context.CancellationToken);
                    context.Reply();
                    break;

                case "discover":
                    context.Reply(await tools.DiscoverAsync(type, parameters, context.CancellationToken));
                    break;

                case "getProperties":
                    context.Reply(await tools.GetPropertiesAsync(type, parameters, context.CancellationToken));
                    break;

                default:
                    context.Fail("Unknown or missing subcommand");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            context.Fail($"{context.Subcommand} failed for {type}: {ex.Message}");
        }
    }
}

/// <summary>
/// <c>ledcolors</c>: <c>ledstream-start</c> pushes the current LED colors as
/// <c>{ command: "ledcolors-ledstream-update", tan, result: { leds: [r, g, b, ...] } }</c> every <c>interval</c> ms
/// (default 100, at least 50) until <c>ledstream-stop</c>.
/// </summary>
public sealed class LedColorsCommand(LightingEngine engine) : IRpcCommand
{
    public const string StreamName = "leds-colors";

    public string Name => "ledcolors";

    public RpcAccess Access => RpcAccess.Authorized;

    public ValueTask ExecuteAsync(RpcContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (context.Subcommand)
        {
            case "ledstream-start" when !context.Session.IsHttp:
                var interval = TimeSpan.FromMilliseconds(Math.Max(context.GetInt("interval") ?? 100, 50));
                context.Reply();
                context.Session.Subscribe(StreamName, new LedStream(context.Session, engine.LedColors, interval, context.Tan));
                break;

            case "ledstream-stop":
                context.Session.Unsubscribe(StreamName);
                context.Reply();
                break;

            default:
                context.Fail("Command not implemented");
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Formats LED stream messages with a reusable writer: one array per message, none per value.</summary>
    private sealed class LedStream : IDisposable
    {
        private readonly PeriodicPush _push;
        private readonly RpcSession _session;
        private readonly LedColorsFeed _feed;
        private readonly int _tan;
        private readonly ArrayBufferWriter<byte> _json = new(4096);
        private byte[] _colors = [];
        private long _lastSequence = -1;

        public LedStream(RpcSession session, LedColorsFeed feed, TimeSpan interval, int tan)
        {
            _session = session;
            _feed = feed;
            _tan = tan;
            _push = new PeriodicPush(interval, Push);
        }

        public void Dispose() => _push.Dispose();

        private void Push()
        {
            // Sequence 0 means the pipeline hasn't published a frame yet: an update now would carry no LEDs.
            var sequence = _feed.Sequence;
            if (sequence == 0 || sequence == _lastSequence)
            {
                return;
            }

            var needed = _feed.LedCount * 3;
            if (_colors.Length != needed)
            {
                _colors = new byte[needed];
            }

            var n = _feed.CopyLatest(_colors);
            if (n == 0)
            {
                return;
            }

            _lastSequence = sequence;
            _json.ResetWrittenCount();
            using (var writer = new Utf8JsonWriter(_json))
            {
                writer.WriteStartObject();
                writer.WriteString("command", "ledcolors-ledstream-update");
                writer.WriteNumber("tan", _tan);
                writer.WriteBoolean("success", true);
                writer.WriteStartObject("result");
                writer.WriteStartArray("leds");
                foreach (var b in _colors.AsSpan(0, n))
                {
                    writer.WriteNumberValue(b);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            _session.SendRaw(_json.WrittenSpan.ToArray());
        }
    }
}
