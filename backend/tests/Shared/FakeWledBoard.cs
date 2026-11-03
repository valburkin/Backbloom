using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Backbloom.Tests.Fakes;

/// <summary>
/// A WLED board's JSON API as the guided setup uses it (<c>/json/info</c>, <c>/json/cfg</c>, <c>/json/state</c>):
/// a new ESP32 board (WLED 0.15) with one WS2812B output of 30 LEDs on GPIO 16, limited to 850 mA. Records every request; a posted cfg replaces the
/// outputs as WLED does, <c>{"rb":true}</c> counts a restart. Served by <see cref="FakeWledHandler"/> (in process) or
/// over HTTP on 127.0.0.1 by the server tests.
/// </summary>
internal sealed class FakeWledBoard
{
    private readonly Lock _lock = new();

    public JsonObject Info { get; } = JsonNode.Parse("""
        { "ver": "0.15.0", "name": "WLED", "arch": "esp32", "mac": "a0b1c2d3e4f5", "ip": "192.0.2.50", "live": false,
          "leds": { "count": 30, "pwr": 0, "fps": 0, "maxpwr": 850 }, "wifi": { "signal": 90 } }
        """)!.AsObject();

    public JsonObject Cfg { get; } = JsonNode.Parse("""
        { "id": { "name": "WLED" },
          "nw": { "ins": [ { "ssid": "", "pskl": 0, "ip": [0, 0, 0, 0] } ] },
          "hw": { "led": { "total": 30, "maxpwr": 850, "ins": [
            { "start": 0, "len": 30, "pin": [16], "order": 0, "rev": false, "skip": 0, "type": 22, "ref": false, "rgbwm": 0, "freq": 0, "maxpwr": 850, "ledma": 55 } ] } } }
        """)!.AsObject();

    public JsonObject State { get; set; } = JsonNode.Parse("""
        { "on": true, "bri": 128, "transition": 7, "ps": 3, "pl": -1,
          "seg": [ { "id": 0, "start": 0, "stop": 30, "on": true, "frz": false, "bri": 255, "fx": 9, "col": [[255, 160, 0]] } ] }
        """)!.AsObject();

    /// <summary>Every request: method, path, body.</summary>
    public List<(string Method, string Path, string? Body)> Requests { get; } = [];

    public int Restarts { get; private set; }

    /// <summary>The settings are locked with a PIN (WLED answers 401).</summary>
    public bool Locked { get; set; }

    /// <summary>How many reads of <c>/json/info</c> after a posted cfg still report the old LED count (WLED rebuilds its outputs a moment later).</summary>
    public int ApplyAfterReads { get; set; }

    private int _pendingCount = -1;
    private int _readsUntilApplied;

    public List<JsonObject> PostedStates
    {
        get
        {
            lock (_lock)
            {
                return [.. Requests.Where(r => r.Method == "POST" && r.Path == "/json/state").Select(r => JsonNode.Parse(r.Body!)!.AsObject())];
            }
        }
    }

    public (HttpStatusCode Status, string Body) Handle(string method, string path, string? body)
    {
        lock (_lock)
        {
            Requests.Add((method, path, body));
            switch (method, path)
            {
                case ("GET", "/json/info"):
                    if (_pendingCount >= 0 && _readsUntilApplied-- <= 0)
                    {
                        Info["leds"]!["count"] = _pendingCount;
                        _pendingCount = -1;
                    }

                    return (HttpStatusCode.OK, Info.ToJsonString());
                case ("GET", "/json/cfg"):
                    return (HttpStatusCode.OK, Cfg.ToJsonString());
                case ("GET", "/json/state"):
                    return (HttpStatusCode.OK, State.ToJsonString());
                case ("POST", "/json/cfg") when Locked:
                    return (HttpStatusCode.Unauthorized, """{"error":1}""");
                case ("POST", "/json/cfg"):
                    var cfg = JsonNode.Parse(body!)!.AsObject();
                    if (cfg["hw"]?["led"] is JsonObject led)
                    {
                        var mine = Cfg["hw"]!["led"]!.AsObject();
                        foreach (var (key, value) in led)
                        {
                            mine[key] = value?.DeepClone();
                        }

                        // WLED applies the outputs (at once, or after ApplyAfterReads): its LED count follows
                        if (led["ins"] is JsonArray ins)
                        {
                            var count = ins.OfType<JsonObject>().Sum(o => o["len"]!.GetValue<int>());
                            if (ApplyAfterReads > 0)
                            {
                                _pendingCount = count;
                                _readsUntilApplied = ApplyAfterReads;
                            }
                            else
                            {
                                Info["leds"]!["count"] = count;
                            }
                        }
                    }

                    if (cfg["def"] is JsonObject boot)
                    {
                        Cfg["def"] = boot.DeepClone();
                    }

                    if (cfg["id"]?["name"] is { } name)
                    {
                        Cfg["id"]!["name"] = name.DeepClone();
                    }

                    if (cfg["nw"]?["ins"]?[0]?["ssid"] is { } ssid)
                    {
                        Cfg["nw"]!["ins"]![0]!["ssid"] = ssid.DeepClone();
                    }

                    return (HttpStatusCode.OK, """{"success":true}""");
                case ("POST", "/json/state"):
                    var state = JsonNode.Parse(body!)!.AsObject();
                    if (state["rb"] is JsonValue rb && rb.GetValue<bool>())
                    {
                        Restarts++;
                    }
                    else
                    {
                        State = state;
                    }

                    return (HttpStatusCode.OK, """{"success":true}""");
                default:
                    return (HttpStatusCode.NotFound, "Not Found");
            }
        }
    }
}

/// <summary>Serves a <see cref="FakeWledBoard"/> to an HttpClient without any socket.</summary>
internal sealed class FakeWledHandler(FakeWledBoard board) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var (status, reply) = board.Handle(request.Method.Method, request.RequestUri!.AbsolutePath, body);
        return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
    }
}
