using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Backbloom.Devices.Drivers.Yeelight;

/// <summary>
/// One Yeelight lamp: the JSON control connection (port 55443) and, in music mode, the stream connection the lamp
/// opens back to us. Port of <c>YeelightLight</c> (sources/led-drivers/net/DriverNetYeelight.cpp), including its
/// command quota wait and its brightness mapping.
/// </summary>
internal sealed partial class YeelightLight : IDisposable
{
    public static readonly string[] AllProperties =
        ["power", "bright", "ct", "rgb", "hue", "sat", "color_mode", "flowing", "delayoff", "music_on", "name", "bg_power", "bg_flowing", "bg_ct", "bg_bright", "bg_hue", "bg_sat", "bg_rgb", "nl_br", "active_mode"];

    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_readTimeout = TimeSpan.FromSeconds(1);

    private readonly YeelightOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private TcpClient? _control;
    private TcpClient? _stream;
    private int _correlationId;
    private long _lastWrite;
    private int _lastRgb;
    private (byte R, byte G, byte B)? _lastHsvColor;
    private int _savedRgb;
    private int _savedBright;
    private string _savedPower = string.Empty;

    public YeelightLight(string host, int port, YeelightOptions options, TimeProvider time, ILogger logger)
    {
        Host = host;
        Port = port;
        _options = options;
        _time = time;
        _logger = logger;
        _lastWrite = time.GetTimestamp();
    }

    public string Host { get; }

    public int Port { get; }

    public bool InError { get; private set; }

    public bool InMusicMode { get; private set; }

    public bool WasOriginallyOn => _savedPower == "on";

    /// <summary>The command JSON (keys sorted like QJsonDocument writes them: id, method, params).</summary>
    public static string Command(int id, string method, JsonArray parameters) =>
        new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString(LampColor.QtJson);

    public async Task<bool> OpenAsync(CancellationToken cancellationToken)
    {
        InError = false;
        if (_control is { Connected: true })
        {
            return true;
        }

        _control?.Dispose();
        _control = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(s_connectTimeout);
            await _control.ConnectAsync(Host, Port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            SetInError(ex.Message);
            return false;
        }
    }

    public void Close()
    {
        _control?.Dispose();
        _control = null;
        _stream?.Dispose();
        _stream = null;
        InMusicMode = false;
    }

    public void Dispose() => Close();

    /// <summary>Takes the connection the lamp opened to the music mode server.</summary>
    public void SetStream(TcpClient stream)
    {
        _stream?.Dispose();
        _stream = stream;
        _stream.NoDelay = true;
    }

    /// <summary>Music mode is on while the lamp's stream connection is up.</summary>
    public bool CheckMusicMode()
    {
        InMusicMode = _stream is { Connected: true };
        return InMusicMode;
    }

    /// <summary>Sends a command and reads its answer: 0 ok, -1 error, -2 command quota exceeded.</summary>
    public async Task<(int Result, JsonArray? Values)> WriteCommandAsync(string method, JsonArray parameters, CancellationToken cancellationToken)
    {
        if (InError || _control is not { Connected: true } control)
        {
            return (-1, null);
        }

        var id = ++_correlationId;
        var stream = control.GetStream();
        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(Command(id, method, parameters) + "\r\n"), cancellationToken);
            if (_time.GetElapsedTime(_lastWrite) < _options.QuotaWait)
            {
                // the C++ code waits the whole quota time before the next command
                await Task.Delay(_options.QuotaWait, _time, cancellationToken);
            }

            var (result, values) = await ReadAnswersAsync(stream, cancellationToken);
            if (result is 0 or -2)
            {
                _lastWrite = _time.GetTimestamp();
            }

            return (result, values);
        }
        catch (IOException ex)
        {
            SetInError($"Write error: {ex.Message}");
            return (-1, null);
        }
    }

    public async Task<bool> SetPowerAsync(bool on, bool smooth, int duration, CancellationToken cancellationToken)
    {
        await SetMusicModeAsync(false, null, 0, cancellationToken);
        if (!on)
        {
            _stream?.Dispose();
            _stream = null;
        }

        var (rc, _) = await WriteCommandAsync("set_power", [on ? "on" : "off", smooth ? "smooth" : "sudden", duration, 2], cancellationToken);
        if (rc > -1 && !on)
        {
            InMusicMode = false;
        }

        return rc > -1;
    }

    public async Task<bool> SetMusicModeAsync(bool on, IPAddress? address, int port, CancellationToken cancellationToken)
    {
        if (on)
        {
            var (rc, _) = await WriteCommandAsync("set_music", [1, address!.ToString(), port], cancellationToken);
            InMusicMode = rc > -1;
            return InMusicMode;
        }

        var wasInError = InError;
        await WriteCommandAsync("set_music", [0], cancellationToken);
        if (InError && !wasInError)
        {
            LogMusicModeOffIgnored(Host);
            InError = false;
        }

        InMusicMode = false;
        return true;
    }

    /// <summary>
    /// <c>set_scene ["color", rgb, brightness(, effect, duration)]</c>; brightness max(R, G, B) * 100 / 255 mapped by
    /// the minimum / maximum / factor settings (below the minimum: off, or the minimum).
    /// </summary>
    public async Task<bool> SetColorRgbAsync(byte r, byte g, byte b, CancellationToken cancellationToken)
    {
        var rgb = (r * 65536) + (g * 256) + b;
        if (rgb == 0)
        {
            rgb = 1;
        }

        if (rgb == _lastRgb)
        {
            return true;
        }

        var (bri, duration) = Brightness(Math.Max(r, Math.Max(g, b)) * 100 / 255);
        JsonArray parameters = ["color", rgb, bri];
        AddTransition(parameters, duration);
        if (!await SceneAsync(parameters, cancellationToken))
        {
            return false;
        }

        _lastRgb = rgb;
        return true;
    }

    /// <summary><c>set_scene ["hsv", hue, saturation, brightness(, effect, duration)]</c> with Yeelight ranges (0-359, 0-100, 0-100).</summary>
    public async Task<bool> SetColorHsvAsync(byte r, byte g, byte b, CancellationToken cancellationToken)
    {
        if (_lastHsvColor == (r, g, b))
        {
            return true;
        }

        var (hue, sat, value) = LampColor.RgbToHsv(r, g, b);
        var (bri, duration) = Brightness(value * 100 / 255);
        JsonArray parameters = ["hsv", hue, sat * 100 / 255, bri];
        AddTransition(parameters, duration);
        if (!await SceneAsync(parameters, cancellationToken))
        {
            return false;
        }

        if (bri == 0)
        {
            InMusicMode = false;
        }

        _lastHsvColor = (r, g, b);
        return true;
    }

    public async Task StoreStateAsync(CancellationToken cancellationToken)
    {
        var properties = await GetPropertiesAsync(cancellationToken);
        _savedPower = properties["power"] is JsonValue p && p.TryGetValue(out string? power) ? power : string.Empty;
        _savedRgb = IntProperty(properties, "rgb");
        _savedBright = IntProperty(properties, "bright");
    }

    public Task<bool> RestoreStateAsync(CancellationToken cancellationToken) => SceneAsync(["color", _savedRgb, _savedBright], cancellationToken);

    public async Task<JsonObject> GetPropertiesAsync(CancellationToken cancellationToken)
    {
        var properties = new JsonObject();
        var (rc, values) = await WriteCommandAsync("get_prop", [.. AllProperties.Select(p => (JsonNode)JsonValue.Create(p))], cancellationToken);
        if (rc > -1 && values is not null)
        {
            for (var i = 0; i < values.Count && i < AllProperties.Length; i++)
            {
                properties[AllProperties[i]] = values[i]?.DeepClone();
            }
        }

        return properties;
    }

    /// <summary>Blue / red color flow, six changes (port of <c>YeelightLight::identify</c>).</summary>
    public async Task<bool> IdentifyAsync(CancellationToken cancellationToken) =>
        (await WriteCommandAsync("set_scene", ["cf", 6, 0, "500,1,100,100,500,1,16711696,10"], cancellationToken)).Result >= 0;

    private static int IntProperty(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue(out string? s) && int.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : 0;

    private (int Brightness, int Duration) Brightness(int bri)
    {
        var duration = _options.TransitionMs;
        if (bri < _options.BrightnessMin)
        {
            if (_options.SwitchOffOnMinimum)
            {
                return (0, _options.TransitionMs + _options.ExtraTimeDarknessMs);
            }

            return (_options.BrightnessMin, duration);
        }

        return (Math.Min(_options.BrightnessMax, (int)(_options.BrightnessFactor * Math.Max(_options.BrightnessMin, bri))), duration);
    }

    private void AddTransition(JsonArray parameters, int duration)
    {
        // older firmware doesn't take these parameters with set_scene, so they are only sent for smooth transitions
        if (_options.Smooth)
        {
            parameters.Add((JsonNode)"smooth");
            parameters.Add((JsonNode)duration);
        }
    }

    private async Task<bool> SceneAsync(JsonArray parameters, CancellationToken cancellationToken)
    {
        if (InMusicMode)
        {
            return await StreamAsync(Command(++_correlationId, "set_scene", parameters), cancellationToken);
        }

        return (await WriteCommandAsync("set_scene", parameters, cancellationToken)).Result >= 0;
    }

    private async Task<bool> StreamAsync(string command, CancellationToken cancellationToken)
    {
        if (InError || _stream is not { Connected: true } stream)
        {
            InMusicMode = false;
            return !InError;
        }

        try
        {
            await stream.GetStream().WriteAsync(Encoding.UTF8.GetBytes(command + "\r\n"), cancellationToken);
            return true;
        }
        catch (IOException)
        {
            // the lamp closed the stream (e.g. switched off by hand): music mode is set up again with the next frame
            InMusicMode = false;
            return true;
        }
    }

    private async Task<(int Result, JsonArray? Values)> ReadAnswersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var rc = -1;
        JsonArray? values = null;
        var buffer = new byte[4096];
        var pending = new StringBuilder();
        var wait = s_readTimeout;
        while (true)
        {
            int n;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(wait);
                try
                {
                    n = await stream.ReadAsync(buffer, timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return (rc, values);
                }
            }

            if (n == 0)
            {
                return (rc, values);
            }

            pending.Append(Encoding.UTF8.GetString(buffer, 0, n));
            int newline;
            while ((newline = pending.ToString().IndexOf('\n', StringComparison.Ordinal)) >= 0)
            {
                var line = pending.ToString(0, newline).Trim();
                pending.Remove(0, newline + 1);
                (rc, values) = HandleAnswer(line, rc, values);
            }

            wait = _options.ResponseSilence;
        }
    }

    private (int Result, JsonArray? Values) HandleAnswer(string line, int rc, JsonArray? values)
    {
        JsonObject? answer;
        try
        {
            answer = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            answer = null;
        }

        if (answer is null)
        {
            SetInError("Got invalid response");
            return (-1, values);
        }

        if (answer["method"] is not null)
        {
            return (0, values); // a notification (props changed)
        }

        if (answer["result"] is JsonArray result)
        {
            return (0, result);
        }

        var error = answer["error"] as JsonObject;
        var code = error is null ? -10010 : DeviceConfig.GetInt(error, "code", 0);
        if (code == -1)
        {
            return (-2, values); // client quota exceeded
        }

        SetInError($"({code}) {(error is null ? "No valid result message" : DeviceConfig.GetString(error, "message"))}");
        return (-1, values);
    }

    private void SetInError(string reason)
    {
        InError = true;
        LogError(Host, reason);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Yeelight {Host} signals error: {Reason}")]
    private partial void LogError(string host, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Yeelight {Host}: ignoring the error when turning music mode off")]
    private partial void LogMusicModeOffIgnored(string host);
}
