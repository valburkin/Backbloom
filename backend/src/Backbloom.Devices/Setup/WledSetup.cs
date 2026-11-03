using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Backbloom.Devices.Drivers;
using Backbloom.Devices.Drivers.Wled;

namespace Backbloom.Devices.Setup;

/// <summary>The checks of the guided setup, lit directly on the board.</summary>
public enum WledTestPattern
{
    /// <summary>Only the output's first LED.</summary>
    First,

    /// <summary>The output's first <c>count</c> LEDs dim, the last of them bright.</summary>
    Range,

    /// <summary>The whole output in one color (red unless given).</summary>
    Color,

    /// <summary>The whole output white (at full brightness only when WLED's current limiter is set).</summary>
    White,

    /// <summary>The whole output dim, some of its LEDs (counted from 1) bright: where a drawn route puts its corners.</summary>
    Points,

    /// <summary>Back to the board's state from before the first check.</summary>
    Off,
}

/// <summary>
/// Sets up a WLED board over its JSON API for the guided setup (<c>hardware wled-*</c>): reads what it is and how its
/// outputs are configured (<c>/json/info</c>, <c>/json/cfg</c>), writes the LED outputs, the current limiter, the name
/// and Wi-Fi (<c>POST /json/cfg</c>, then a restart with <c>{"rb":true}</c> on <c>/json/state</c> when the change
/// needs one) and lights the checks with individual LED control (<c>seg.i</c> on <c>/json/state</c>, low
/// brightness), saving the board's state on the first check and restoring it on <c>off</c>. Only boards on the local
/// network are contacted (<see cref="WledHosts"/>), and only the one the request names; Wi-Fi passwords are sent to it
/// and never logged or stored.
/// </summary>
/// <remarks>
/// WLED rebuilds its outputs from the whole <c>hw.led.ins</c> list it receives, so a write sends every output,
/// based on what the board reported (fields Backbloom doesn't set keep their values). The limit is written both as
/// WLED's global budget (<c>hw.led.maxpwr</c>: enforced by 0.14 and by 0.16 and newer) and split over the outputs by
/// their LED count (<c>hw.led.ins[].maxpwr</c>: the only limit 0.15 enforces; what 0.15's own settings page writes),
/// with the LED current on every output (<c>ledma</c>; also the global <c>ledma</c> 0.14 reads).
/// </remarks>
public sealed partial class WledSetup : IDisposable
{
    /// <summary>Brightness of the checks while the board's current limiter isn't set (about 16 %).</summary>
    public const int LowBrightness = 40;

    /// <summary>The lowest current limit WLED accepts (0 turns the limiter off).</summary>
    public const int MinimumCurrentLimit = 250;

    /// <summary>How long after the last check a state saved by the first is kept for <see cref="WledTestPattern.Off"/>.</summary>
    public static readonly TimeSpan SavedStateLifetime = TimeSpan.FromMinutes(30);

    private const string Black = "000000";
    private const string White = "FFFFFF";

    // WLED gamma-corrects seg.i colors (about 2.8): 0xA0 comes out near 27 % of full, still visible at LowBrightness
    private const string Dim = "A0A0A0";
    private const string Red = "FF0000";

    private readonly DriverContext _context;
    private readonly JsonRest _rest;
    private readonly ILogger _logger;
    private readonly TimeSpan _restartDelay;
    private readonly Func<IPAddress, bool> _allowAddress;
    private readonly ConcurrentDictionary<string, SavedState> _savedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SavedState> _savedConfigs = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _flashStep;
    private readonly TimeSpan _restartWait;
    private readonly TimeSpan _applyWait;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="restartDelay">Wait between writing the settings and the restart, so WLED has saved them (1 s).</param>
    /// <param name="allowAddress">
    /// Which resolved addresses may be contacted; <see cref="WledHosts.IsLanAddress"/> by default. Tests allow their
    /// fakes on 127.0.0.1 with it; it is not a setting.
    /// </param>
    /// <param name="flashStep">How long the identify flash is on and off (400 ms).</param>
    /// <param name="restartWait">How long a board may take to come back after a restart (30 s).</param>
    /// <param name="applyWait">After a write the board applies without a restart: how long it may take to build its outputs again (500 ms).</param>
    public WledSetup(DriverContext context, TimeSpan? restartDelay = null, Func<IPAddress, bool>? allowAddress = null, TimeSpan? flashStep = null, TimeSpan? restartWait = null, TimeSpan? applyWait = null)
    {
        _applyWait = applyWait ?? TimeSpan.FromMilliseconds(500);
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _rest = new JsonRest(context.Http);
        _logger = context.LoggerFactory.CreateLogger<WledSetup>();
        _restartDelay = restartDelay ?? TimeSpan.FromSeconds(1);
        _flashStep = flashStep ?? TimeSpan.FromMilliseconds(400);
        _restartWait = restartWait ?? TimeSpan.FromSeconds(30);
        _allowAddress = allowAddress ?? (a => WledHosts.IsLanAddress(a));
    }

    /// <summary>Hosts with a state saved by a check (restored by <see cref="WledTestPattern.Off"/>).</summary>
    public IReadOnlyCollection<string> HostsWithSavedState => [.. _savedStates.Keys];

    /// <summary>
    /// <c>{ name, version, chip, mac, address, ledCount, outputs: [{ index, strip, type, pin, clockPin, length, start,
    /// colorOrder, reverse }], maxCurrent, ledCurrent, wifi: { ssid } }</c>.
    /// </summary>
    public async Task<JsonObject> ReadAsync(string host, CancellationToken cancellationToken)
    {
        var root = await RootAsync(host, cancellationToken);
        var info = await GetObjectAsync(host, root, "json/info", cancellationToken);
        var cfg = await GetObjectAsync(host, root, "json/cfg", cancellationToken);
        return Describe(info, cfg);
    }

    /// <summary>The board's <c>/json/info</c> within <paramref name="timeout"/> (the search and the hotspot visit).</summary>
    public async Task<JsonObject> ReadInfoAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            var root = await RootAsync(host, limit.Token);
            return await GetObjectAsync(host, root, "json/info", limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HardwareSetupException($"Couldn’t reach {host}");
        }
    }

    /// <summary>The <see cref="ReadAsync"/> shape from WLED's info and cfg (internal for the tests).</summary>
    internal static JsonObject Describe(JsonObject info, JsonObject cfg)
    {
        var led = Led(cfg);
        var ins = Outputs(cfg);
        var outputs = new JsonArray();
        for (var i = 0; i < ins.Count; i++)
        {
            var o = ins[i];
            var type = Int(o["type"]) ?? WledStrips.Ws281x;
            var pins = (o["pin"] as JsonArray ?? []).Select(Int).ToList();
            var ma = Int(o["ledma"]) ?? 0;
            outputs.Add((JsonNode)new JsonObject
            {
                ["index"] = i,
                ["strip"] = WledStrips.ForType(type & 0x7F, ma)?.Id,
                ["type"] = type & 0x7F,
                ["pin"] = pins.Count > 0 ? pins[0] : null,
                ["clockPin"] = WledStrips.IsTwoWire(type & 0x7F) && pins.Count > 1 ? pins[1] : null,
                ["length"] = Int(o["len"]) ?? 0,
                ["start"] = Int(o["start"]) ?? 0,
                ["colorOrder"] = WledStrips.OrderName(Int(o["order"]) ?? 0),
                ["reverse"] = Bool(o["rev"]) ?? false,
            });
        }

        var ledma = ins.Select(o => Int(o["ledma"]) ?? 0).FirstOrDefault(m => m > 0);
        if (ledma == 0)
        {
            ledma = Int(led?["ledma"]) ?? 0;
        }

        var wifi = ((cfg["nw"] as JsonObject)?["ins"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var ssid = wifi is null ? null : DeviceConfig.GetString(wifi, "ssid");
        var name = (cfg["id"] as JsonObject) is { } id ? DeviceConfig.GetString(id, "name") : string.Empty;
        return new JsonObject
        {
            ["name"] = name.Length > 0 ? name : DeviceConfig.GetString(info, "name"),
            ["version"] = DeviceConfig.GetString(info, "ver"),
            ["chip"] = DeviceConfig.GetString(info, "arch"),
            ["mac"] = FormatMac(DeviceConfig.GetString(info, "mac")),
            ["address"] = DeviceConfig.GetString(info, "ip") is { Length: > 0 } ip ? ip : null,
            ["ledCount"] = Int((info["leds"] as JsonObject)?["count"]) ?? ins.Sum(o => Int(o["len"]) ?? 0),
            ["outputs"] = outputs,
            ["maxCurrent"] = WledConfig.Read(cfg, DeviceConfig.GetString(info, "ver")).PowerLimitMilliamps ?? 0,
            ["ledCurrent"] = ledma switch
            {
                0 => null,
                WledStrips.Ws2815Marker => WledStrips.Ws2815Milliamps,
                _ => ledma,
            },
            ["wifi"] = new JsonObject { ["ssid"] = string.IsNullOrEmpty(ssid) ? null : ssid },
        };
    }

    /// <summary>
    /// Writes <c>{ name?, outputs?, maxCurrent?, ledCurrent?, wifi?: { ssid, password } }</c> (see the protocol) and
    /// restarts the board when the change needs it: new Wi-Fi, or outputs added, removed or with another strip type
    /// or pin. Returns whether it restarts. A board another program streams to is refused.
    /// </summary>
    public async Task<bool> WriteAsync(string host, JsonObject request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = await RootAsync(host, cancellationToken);
            var info = await GetObjectAsync(host, root, "json/info", cancellationToken);
            RefuseLive(info);
            var cfg = await GetObjectAsync(host, root, "json/cfg", cancellationToken);
            var (body, restart) = BuildConfig(cfg, request);
            if (body.Count == 0)
            {
                return false;
            }

            // a shorter or removed output leaves LEDs lit with their last colors: every LED black first
            var before = Outputs(cfg);
            var after = ((body["hw"] as JsonObject)?["led"] as JsonObject)?["ins"] as JsonArray;
            if (after is not null && (after.Count < before.Count || after.OfType<JsonObject>().Select((o, i) => i < before.Count && (Int(o["len"]) ?? 0) < (Int(before[i]["len"]) ?? 0)).Any(shorter => shorter)))
            {
                await BlackOutAsync(host, root, cancellationToken);
            }

            await PostConfigAsync(host, root, body, cancellationToken);
            var sections = string.Join(", ", body.Select(p => p.Key));
            LogWritten(host, sections, restart);
            if (restart)
            {
                await RestartAsync(root, cancellationToken);
            }
            else if (body["hw"] is JsonObject hw)
            {
                await WaitUntilAppliedAsync(host, root, hw, cancellationToken);
            }

            return restart;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The body for <c>POST /json/cfg</c> and whether the board restarts for it (internal for the tests). Validates the
    /// request and throws <see cref="HardwareSetupException"/> with what is wrong.
    /// </summary>
    internal static (JsonObject Body, bool Restart) BuildConfig(JsonObject cfg, JsonObject request)
    {
        var body = new JsonObject();
        var restart = false;
        if (request["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? name))
        {
            name = name.Trim();
            if (name.Length is 0 or > 32)
            {
                throw new HardwareSetupException("The board’s name must have 1 to 32 characters");
            }

            body["id"] = new JsonObject { ["name"] = name };
        }

        var maxCurrent = OptionalInt(request, "maxCurrent");
        if (maxCurrent is { } max && max != 0 && (max < MinimumCurrentLimit || max > 65000))
        {
            throw new HardwareSetupException($"maxCurrent must be 0 (no limit) or {MinimumCurrentLimit} to 65000 mA");
        }

        var ledCurrent = OptionalInt(request, "ledCurrent");
        if (ledCurrent is { } ma && (ma < 1 || ma > 254))
        {
            throw new HardwareSetupException("ledCurrent must be 1 to 254 mA");
        }

        var existing = Outputs(cfg);
        List<JsonObject>? outputs = null;
        if (request["outputs"] is JsonArray requested)
        {
            if (requested.Count == 0)
            {
                throw new HardwareSetupException("outputs needs at least one output");
            }

            outputs = [];
            var start = 0;
            for (var i = 0; i < requested.Count; i++)
            {
                if (requested[i] is not JsonObject r)
                {
                    throw new HardwareSetupException($"outputs[{i}] must be an object");
                }

                var before = i < existing.Count ? existing[i] : null;
                var output = BuildOutput(i, r, before, existing.Count > 0 ? existing[0] : null, start);
                restart |= before is null || Int(before["type"]) != Int(output["type"]) || !SamePins(before["pin"], output["pin"]);
                start = (Int(output["start"]) ?? 0) + (Int(output["len"]) ?? 0);
                outputs.Add(output);
            }

            restart |= requested.Count != existing.Count;
        }
        else if (request["outputs"] is not null)
        {
            throw new HardwareSetupException("outputs must be a list");
        }

        if (outputs is not null || maxCurrent is not null || ledCurrent is not null)
        {
            outputs ??= [.. existing.Select(o => o.DeepClone().AsObject())];
            var requestedStrips = request["outputs"] as JsonArray;
            for (var i = 0; i < outputs.Count; i++)
            {
                var o = outputs[i];
                var stripId = ((requestedStrips?[i] as JsonObject)?["strip"] as JsonValue)?.TryGetValue(out string? id) == true ? id : null;
                var ws2815 = stripId is not null
                    ? WledStrips.Find(stripId)?.Ws2815Current == true
                    : Int(o["type"]) == WledStrips.Ws281x && Int(o["ledma"]) == WledStrips.Ws2815Marker;
                if (ledCurrent is { } current)
                {
                    o["ledma"] = ws2815 ? WledStrips.Ws2815Marker : current;
                }
                else if (ws2815)
                {
                    o["ledma"] = WledStrips.Ws2815Marker;
                }
                else if (o["ledma"] is null || Int(o["ledma"]) == WledStrips.Ws2815Marker)
                {
                    o["ledma"] = WledStrips.DefaultLedMilliamps;
                }
            }

            // the limit as requested, else the one the board has, written for every WLED version (see the remarks)
            var limit = maxCurrent ?? ExistingLimit(cfg);
            var split = SplitLimit(limit, [.. outputs.Select(o => Int(o["len"]) ?? 0)]);
            for (var i = 0; i < outputs.Count; i++)
            {
                outputs[i]["maxpwr"] = split[i];
            }

            var led = new JsonObject { ["maxpwr"] = limit, ["ins"] = new JsonArray([.. outputs]) };
            if (ledCurrent is { } global)
            {
                led["ledma"] = global;
            }

            body["hw"] = new JsonObject { ["led"] = led };

            // the board starts dark after a restart or power cut (not WLED's own bright orange): Backbloom lights it
            if (request["outputs"] is not null)
            {
                body["def"] = new JsonObject { ["on"] = false };
            }
        }

        if (request["wifi"] is JsonObject wifi)
        {
            var ssid = (wifi["ssid"] as JsonValue)?.TryGetValue(out string? s) == true ? s : null;
            var password = (wifi["password"] as JsonValue)?.TryGetValue(out string? p) == true ? p : null;
            if (string.IsNullOrEmpty(ssid) || System.Text.Encoding.UTF8.GetByteCount(ssid) > 32)
            {
                throw new HardwareSetupException("wifi.ssid must have 1 to 32 characters");
            }

            if (password is null || System.Text.Encoding.UTF8.GetByteCount(password) > 64)
            {
                throw new HardwareSetupException("wifi.password must be a text of at most 64 characters");
            }

            var current = ((cfg["nw"] as JsonObject)?["ins"] as JsonArray)?.FirstOrDefault() as JsonObject;
            var network = current?.DeepClone().AsObject() ?? [];
            network.Remove("pskl");
            network["ssid"] = ssid;
            network["psk"] = password;
            body["nw"] = new JsonObject { ["ins"] = new JsonArray(network) };
            restart = true;
        }
        else if (request["wifi"] is not null)
        {
            throw new HardwareSetupException("wifi must be { ssid, password }");
        }

        return (body, restart);
    }

    /// <summary>
    /// A limit split over outputs by their LED count (as WLED 0.15's settings page does); all 0 without a limit. WLED
    /// 0.15 doesn't limit an output whose own limit is below its share of the 100 mA it keeps for the ESP
    /// (100 / outputs), so every output gets at least that share, rounded up, taken from the largest parts; the parts
    /// add up to the limit (the smallest limit, 250 mA, leaves room for that with any practical number of outputs; only
    /// beyond it they add up to more, as no output may go unlimited).
    /// </summary>
    internal static int[] SplitLimit(int limit, IReadOnlyList<int> lengths)
    {
        var split = new int[lengths.Count];
        var total = lengths.Sum(l => Math.Max(0, l));
        if (limit <= 0 || split.Length == 0)
        {
            return split;
        }

        var least = (Drivers.Wled.WledConfig.EspMilliamps + split.Length - 1) / split.Length;
        var given = 0;
        for (var i = 0; i < split.Length; i++)
        {
            split[i] = total == 0 ? limit / split.Length : (int)((long)limit * Math.Max(0, lengths[i]) / total);
            split[i] = Math.Max(least, split[i]);
            given += split[i];
        }

        // the rounding remainder to the longest output, an excess (from the shares) taken from the largest parts
        var longest = Enumerable.Range(0, split.Length).MaxBy(i => lengths[i]);
        if (given < limit)
        {
            split[longest] += limit - given;
        }

        for (var excess = given - limit; excess > 0;)
        {
            var largest = Enumerable.Range(0, split.Length).MaxBy(i => split[i]);
            var take = Math.Min(excess, split[largest] - least);
            if (take <= 0)
            {
                break;
            }

            split[largest] -= take;
            excess -= take;
        }

        return split;
    }

    /// <summary>Lights a check on output <paramref name="output"/> (see <see cref="WledTestPattern"/>).</summary>
    /// <param name="count">For <see cref="WledTestPattern.Range"/>: how many LEDs (1 to the output's length).</param>
    /// <param name="color">For <see cref="WledTestPattern.Color"/> and <see cref="WledTestPattern.First"/>: the color.</param>
    public Task TestAsync(string host, int output, WledTestPattern pattern, int? count, (byte R, byte G, byte B)? color, CancellationToken cancellationToken) =>
        TestAsync(host, output, pattern, count, color, null, cancellationToken);

    /// <summary>Lights a check on output <paramref name="output"/> (see <see cref="WledTestPattern"/>).</summary>
    /// <param name="leds">For <see cref="WledTestPattern.Points"/>: the LEDs to light bright (1 to the output's length).</param>
    public async Task TestAsync(string host, int output, WledTestPattern pattern, int? count, (byte R, byte G, byte B)? color, IReadOnlyList<int>? leds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = await RootAsync(host, cancellationToken);
            var now = _context.Time.GetUtcNow();
            if (_savedStates.TryGetValue(host, out var kept) && now - kept.At > SavedStateLifetime)
            {
                // no check for too long: it no longer says how the board was before
                _savedStates.TryRemove(host, out _);
            }
            else if (kept is not null)
            {
                // each check keeps it for another 30 minutes (wiring between checks may take a while)
                _savedStates[host] = kept with { At = now };
            }

            // and the LED settings a probe saved
            if (_savedConfigs.TryGetValue(host, out var probe) && now - probe.At <= SavedStateLifetime)
            {
                _savedConfigs[host] = probe with { At = now };
            }

            if (pattern == WledTestPattern.Off)
            {
                if (_savedStates.TryRemove(host, out var saved))
                {
                    await PostStateAsync(host, root, saved.State, cancellationToken);
                    LogRestored(host);
                }

                return;
            }

            var info = await GetObjectAsync(host, root, "json/info", cancellationToken);
            RefuseLive(info);
            var cfg = await GetObjectAsync(host, root, "json/cfg", cancellationToken);
            var state = await GetObjectAsync(host, root, "json/state", cancellationToken);
            if (!_savedStates.ContainsKey(host))
            {
                _savedStates[host] = new SavedState(Restorable(state), now);
            }

            await PostStateAsync(host, root, TestState(info, cfg, state, output, pattern, count, color, leds), cancellationToken);
            LogTest(host, output, pattern);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// The <c>/json/state</c> body of a check (internal for the tests): segment 0 over every LED with the check's colors
    /// (everything else black), the board's other segments off.
    /// </summary>
    internal static JsonObject TestState(JsonObject info, JsonObject cfg, JsonObject state, int output, WledTestPattern pattern, int? count, (byte R, byte G, byte B)? color, IReadOnlyList<int>? points = null)
    {
        var outputs = Outputs(cfg);
        if (output < 0 || output >= outputs.Count)
        {
            throw new HardwareSetupException(outputs.Count == 0 ? "The board has no LED output" : $"The board has no output {output} (it has {outputs.Count})");
        }

        var start = Int(outputs[output]["start"]) ?? 0;
        var length = Math.Max(1, Int(outputs[output]["len"]) ?? 1);
        var total = Math.Max(Int((info["leds"] as JsonObject)?["count"]) ?? 0, outputs.Max(o => (Int(o["start"]) ?? 0) + (Int(o["len"]) ?? 0)));
        var hex = color is { } c ? string.Create(CultureInfo.InvariantCulture, $"{c.R:X2}{c.G:X2}{c.B:X2}") : null;
        var leds = new JsonArray((JsonNode)0, total, Black);
        var brightness = LowBrightness;
        switch (pattern)
        {
            case WledTestPattern.First:
                leds.Add((JsonNode)start);
                leds.Add((JsonNode)(hex ?? White));
                break;
            case WledTestPattern.Range:
                var n = count ?? length;
                if (n < 1 || n > length)
                {
                    throw new HardwareSetupException($"count must be 1 to {length} (the output's LEDs)");
                }

                if (n > 1)
                {
                    leds.Add((JsonNode)start);
                    leds.Add((JsonNode)(start + n - 1));
                    leds.Add((JsonNode)Dim);
                }

                leds.Add((JsonNode)(start + n - 1));
                leds.Add((JsonNode)White);
                break;
            case WledTestPattern.Color:
                leds.Add((JsonNode)start);
                leds.Add((JsonNode)(start + length));
                leds.Add((JsonNode)(hex ?? Red));
                break;
            case WledTestPattern.Points:
                if (points is null || points.Count == 0 || points.Count > 64 || points.Any(p => p < 1 || p > length))
                {
                    throw new HardwareSetupException($"leds must list 1 to 64 LEDs, each 1 to {length} (the output's LEDs)");
                }

                leds.Add((JsonNode)start);
                leds.Add((JsonNode)(start + length));
                leds.Add((JsonNode)Dim);
                foreach (var p in points.Distinct().Order())
                {
                    leds.Add((JsonNode)(start + p - 1));
                    leds.Add((JsonNode)White);
                }

                break;
            case WledTestPattern.White:
                leds.Add((JsonNode)start);
                leds.Add((JsonNode)(start + length));
                leds.Add((JsonNode)White);
                // the tested output itself must be limited (0.15 limits each output on its own)
                brightness = WledConfig.OutputLimited(cfg, DeviceConfig.GetString(info, "ver"), output) ? 255 : LowBrightness;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pattern));
        }

        return Lit(state, total, leds, brightness);
    }

    // Segment 0 over all LEDs showing "i", the board's other segments off.
    private static JsonObject Lit(JsonObject state, int total, JsonArray leds, int brightness)
    {
        var segments = new JsonArray(new JsonObject
        {
            ["id"] = 0,
            ["start"] = 0,
            ["stop"] = total,
            ["on"] = true,
            ["bri"] = 255,
            ["fx"] = 0,
            ["i"] = leds,
        });
        foreach (var other in (state["seg"] as JsonArray ?? []).OfType<JsonObject>().Select(s => Int(s["id"])).Where(id => id is > 0))
        {
            segments.Add((JsonNode)new JsonObject { ["id"] = other, ["on"] = false });
        }

        return new JsonObject { ["on"] = true, ["bri"] = brightness, ["transition"] = 0, ["seg"] = segments };
    }

    // Every LED the board drives: its count, or the outputs' end when that is more.
    private static int TotalLeds(JsonObject info, JsonObject cfg) =>
        Math.Max(Int((info["leds"] as JsonObject)?["count"]) ?? 0, Outputs(cfg).Select(o => (Int(o["start"]) ?? 0) + (Int(o["len"]) ?? 0)).DefaultIfEmpty(0).Max());


    /// <summary>
    /// Flashes the whole board white three times at low brightness (the supply may not be known yet: WLED's own limiter
    /// guesses the current), then puts its state back: for a Blink button, and to confirm a board right after it joined
    /// the Wi-Fi.
    /// </summary>
    public async Task IdentifyAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = await RootAsync(host, cancellationToken);
            var info = await GetObjectAsync(host, root, "json/info", cancellationToken);
            RefuseLive(info);
            var cfg = await GetObjectAsync(host, root, "json/cfg", cancellationToken);
            var state = await GetObjectAsync(host, root, "json/state", cancellationToken);
            var total = Math.Max(1, TotalLeds(info, cfg));
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    await PostStateAsync(host, root, Lit(state, total, new JsonArray((JsonNode)0, total, White), LowBrightness), cancellationToken);
                    await Task.Delay(_flashStep, _context.Time, cancellationToken);
                    await PostStateAsync(host, root, Lit(state, total, new JsonArray((JsonNode)0, total, Black), LowBrightness), cancellationToken);
                    await Task.Delay(_flashStep, _context.Time, cancellationToken);
                }
            }
            finally
            {
                await PostStateAsync(host, root, Restorable(state), CancellationToken.None);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How many LEDs at the start of each candidate output of a probe light up.</summary>
    public const int ProbeLeds = 20;

    /// <summary>
    /// How long each candidate output of a probe is: 2000 LEDs over the candidates, 60 to 300 each. The LEDs after the
    /// lit ones get black, so a strip doesn't keep showing colors from before.
    /// </summary>
    public static int ProbeLength(int candidates) => Math.Clamp(2000 / Math.Max(1, candidates), 60, 300);

    /// <summary>
    /// Finding the strips: saves the board's LED settings (once, kept like a check's state), writes one WS281x output
    /// of <see cref="ProbeLength"/> LEDs per candidate pin of <paramref name="round"/> (<see cref="WledPins"/>, without
    /// the pins the board uses for buttons, relay, IR, I2C and SPI), keeping its current limit, name and Wi-Fi;
    /// restarts it, waits until it is back and lights the first <see cref="ProbeLeds"/> LEDs of every candidate white
    /// at low brightness, the rest black. Returns the candidates in output order.
    /// </summary>
    public async Task<IReadOnlyList<(int Output, int Pin)>> ProbeStartAsync(string host, int round, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (round is not (1 or 2))
        {
            throw new HardwareSetupException("round must be 1 or 2");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = await RootAsync(host, cancellationToken);
            var info = await GetObjectAsync(host, root, "json/info", cancellationToken);
            RefuseLive(info);
            var cfg = await GetObjectAsync(host, root, "json/cfg", cancellationToken);
            var now = _context.Time.GetUtcNow();

            // the LED settings and the boot setting (the probe's write turns "on at boot" off) for keep: false
            var original = _savedConfigs.TryGetValue(host, out var kept) && now - kept.At <= SavedStateLifetime
                ? kept.State
                : new JsonObject { ["led"] = Led(cfg)?.DeepClone(), ["def"] = cfg["def"]?.DeepClone() };
            _savedConfigs[host] = new SavedState(original, now);

            // the state for `off`, from before the search (the probe restarts the board): dark, as the found strips
            // start (Backbloom lights them)
            if (!_savedStates.TryGetValue(host, out var before) || now - before.At > SavedStateLifetime)
            {
                var dark = Restorable(await GetObjectAsync(host, root, "json/state", cancellationToken));
                dark["on"] = false;
                _savedStates[host] = new SavedState(dark, now);
            }

            var pins = WledPins.Candidates(DeviceConfig.GetString(info, "arch"), round)
                .Except(WledPins.UsedPins(cfg))
                .Take(WledPins.MaxOutputs(DeviceConfig.GetString(info, "arch"), DeviceConfig.GetString(info, "ver")))
                .ToList();
            if (pins.Count == 0)
            {
                throw new HardwareSetupException("There are no more pins to try on this board");
            }

            var length = ProbeLength(pins.Count);
            var outputs = new JsonArray([.. pins.Select(p => (JsonNode)new JsonObject { ["strip"] = "ws2812b", ["length"] = length, ["pin"] = p, ["colorOrder"] = "GRB" })]);
            var (body, _) = BuildConfig(cfg, new JsonObject { ["outputs"] = outputs });
            var uptime = Int(info["uptime"]);
            await PostConfigAsync(host, root, body, cancellationToken);
            var pinList = string.Join(", ", pins);
            LogProbe(host, round, pinList);
            await RestartAsync(root, cancellationToken);
            await WaitUntilBackAsync(host, root, uptime, cancellationToken);
            var state = await GetObjectAsync(host, root, "json/state", cancellationToken);
            var total = pins.Count * length;
            var leds = new JsonArray((JsonNode)0, total, Black);
            for (var i = 0; i < pins.Count; i++)
            {
                leds.Add((JsonNode)(i * length));
                leds.Add((JsonNode)((i * length) + ProbeLeds));
                leds.Add((JsonNode)White);
            }

            await PostStateAsync(host, root, Lit(state, total, leds, LowBrightness), cancellationToken);
            return [.. pins.Select((p, i) => (i, p))];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The end of finding the strips: with <paramref name="keep"/> false every LED goes black and the saved LED and
    /// boot settings go back (and the board restarts: returns true); with it true they are forgotten (the found strips
    /// were written with <see cref="WriteAsync"/> first). Nothing saved (or saved more than 30 minutes before the last
    /// step): nothing to do.
    /// </summary>
    public async Task<bool> ProbeEndAsync(string host, bool keep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_savedConfigs.TryRemove(host, out var saved) || _context.Time.GetUtcNow() - saved.At > SavedStateLifetime)
            {
                return false;
            }

            if (keep)
            {
                // the found strips were written first (that write turns the test outputs' LEDs black)
                return false;
            }

            // every test output black first: LEDs past the board's own (shorter) outputs keep the last colors they got
            var root = await RootAsync(host, cancellationToken);
            var info = await GetObjectAsync(host, root, "json/info", cancellationToken);
            RefuseLive(info);
            await BlackOutAsync(host, root, cancellationToken);
            var restore = new JsonObject { ["hw"] = new JsonObject { ["led"] = saved.State["led"]?.DeepClone() } };
            if (saved.State["def"] is JsonObject boot)
            {
                restore["def"] = boot.DeepClone();
            }

            await PostConfigAsync(host, root, restore, cancellationToken);
            LogProbeRestored(host);
            await RestartAsync(root, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Every LED of the board black (the board's other segments off); like a check, it saves the board's state first
    // (when no check has), for `off`.
    private async Task BlackOutAsync(string host, Uri root, CancellationToken cancellationToken)
    {
        var info = await GetObjectAsync(host, root, "json/info", cancellationToken);
        var cfg = await GetObjectAsync(host, root, "json/cfg", cancellationToken);
        var state = await GetObjectAsync(host, root, "json/state", cancellationToken);
        var now = _context.Time.GetUtcNow();
        if (!_savedStates.TryGetValue(host, out var kept) || now - kept.At > SavedStateLifetime)
        {
            _savedStates[host] = new SavedState(Restorable(state), now);
        }
        var total = Math.Max(1, TotalLeds(info, cfg));
        await PostStateAsync(host, root, Lit(state, total, new JsonArray((JsonNode)0, total, Black), LowBrightness), cancellationToken);
    }

    // After a restart: /json/info answers again, from a board that restarted (its uptime is lower) when it reports one.
    private async Task<JsonObject> WaitUntilBackAsync(string host, Uri root, int? uptimeBefore, CancellationToken cancellationToken)
    {
        var deadline = _context.Time.GetUtcNow() + _restartWait;
        while (true)
        {
            try
            {
                var info = await GetObjectAsync(host, root, "json/info", cancellationToken);
                if (uptimeBefore is not { } before || Int(info["uptime"]) is not { } now || now < before)
                {
                    return info;
                }
            }
            catch (HardwareSetupException) when (_context.Time.GetUtcNow() < deadline)
            {
                // still restarting
            }

            if (_context.Time.GetUtcNow() >= deadline)
            {
                throw new HardwareSetupException($"The board at {host} didn’t come back after restarting: check its power, then try again");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), _context.Time, cancellationToken);
        }
    }

    private async Task PostConfigAsync(string host, Uri root, JsonObject body, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(host, () => _rest.PostAsync(new Uri(root, "json/cfg"), body.ToJsonString(), cancellationToken));
        if (reply.Status == (int)HttpStatusCode.Unauthorized)
        {
            throw new HardwareSetupException("The board’s settings are locked with a PIN: unlock them in WLED first");
        }

        if (!reply.IsSuccess)
        {
            throw new HardwareSetupException($"The board didn’t take the settings (HTTP {reply.Status})");
        }
    }

    /// <summary>
    /// The state as saved for restoring: without the preset and playlist (posting them would start them again) and the
    /// segments' bounds (the outputs may have changed since; the segments keep the ones the checks gave them).
    /// </summary>
    internal static JsonObject Restorable(JsonObject state)
    {
        var saved = state.DeepClone().AsObject();
        saved.Remove("ps");
        saved.Remove("pl");
        saved["transition"] = 0;
        foreach (var segment in (saved["seg"] as JsonArray ?? []).OfType<JsonObject>())
        {
            segment.Remove("start");
            segment.Remove("stop");
            segment.Remove("len");

            // the check froze the segment; the saved effect runs again
            segment["frz"] = false;
        }

        return saved;
    }

    internal static string? FormatMac(string mac) =>
        mac.Length == 12 && mac.All(Uri.IsHexDigit)
            ? string.Join(':', Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2).ToUpperInvariant()))
            : mac.Length > 0 ? mac : null;

    private static void RefuseLive(JsonObject info)
    {
        if (Bool(info["live"]) == true)
        {
            throw new HardwareSetupException("Another program is sending colors to this board right now: stop it first");
        }
    }

    // The limit the board has: the global one, else the sum of the outputs' own (with an LED current); 0 for none.
    private static int ExistingLimit(JsonObject cfg)
    {
        var global = Int(Led(cfg)?["maxpwr"]) ?? 0;
        return global > 0 ? global : Outputs(cfg).Where(o => (Int(o["ledma"]) ?? 0) > 0).Sum(o => Int(o["maxpwr"]) ?? 0);
    }

    private static JsonObject BuildOutput(int index, JsonObject r, JsonObject? before, JsonObject? first, int defaultStart)
    {
        var where = $"outputs[{index}]";
        var stripId = (r["strip"] as JsonValue)?.TryGetValue(out string? sid) == true ? sid : null;
        var strip = WledStrips.Find(stripId);
        var type = strip?.WledType ?? OptionalInt(r, "type");
        if (type is null)
        {
            throw new HardwareSetupException(stripId is null ? $"{where}.strip is required" : $"{where}: unknown strip type '{stripId}'");
        }

        var length = OptionalInt(r, "length") ?? 0;
        if (length is < 1 or > 16384)
        {
            throw new HardwareSetupException($"{where}.length must be 1 to 16384 LEDs");
        }

        var start = OptionalInt(r, "start") ?? defaultStart;
        if (start < 0)
        {
            throw new HardwareSetupException($"{where}.start can't be negative");
        }

        var beforePins = (before?["pin"] as JsonArray ?? []).Select(Int).ToList();
        var pin = OptionalInt(r, "pin") ?? (beforePins.Count > 0 ? beforePins[0] : null);
        if (pin is not ({ } p and >= 0 and <= 48))
        {
            throw new HardwareSetupException(pin is null ? $"{where}.pin is required (the board has no output {index} yet)" : $"{where}.pin must be a GPIO number (0 to 48)");
        }

        var twoWire = WledStrips.IsTwoWire(type.Value);
        var clock = OptionalInt(r, "clockPin") ?? (beforePins.Count > 1 && WledStrips.IsTwoWire((Int(before?["type"]) ?? 0) & 0x7F) ? beforePins[1] : null);
        if (twoWire && clock is not (>= 0 and <= 48))
        {
            throw new HardwareSetupException($"{where}.clockPin is required for {stripId ?? "this strip type"} (data and clock)");
        }

        var orderName = (r["colorOrder"] as JsonValue)?.TryGetValue(out string? on) == true ? on : null;
        int order;
        if (orderName is not null)
        {
            order = WledStrips.OrderNumber(orderName);
            if (order < 0)
            {
                throw new HardwareSetupException($"{where}.colorOrder must be one of {string.Join(", ", WledStrips.ColorOrders)}");
            }
        }
        else
        {
            order = before is not null && Int(before["type"]) == type ? (Int(before["order"]) ?? 0) & 0x0F : WledStrips.OrderNumber(strip?.ColorOrder ?? "GRB");
        }

        // the white-channel swap in the upper bits stays while the strip type does
        var swap = before is not null && Int(before["type"]) == type ? (Int(before["order"]) ?? 0) & 0xF0 : 0;
        var output = (before ?? first)?.DeepClone().AsObject() ?? [];
        output["type"] = type.Value;
        output["pin"] = twoWire ? new JsonArray(p, clock!.Value) : new JsonArray(p);
        output["start"] = start;
        output["len"] = length;
        output["order"] = order | swap;
        output["rev"] = (r["reverse"] as JsonValue)?.TryGetValue(out bool reverse) == true ? reverse : (before is not null && (Bool(before["rev"]) ?? false));
        if (before is null)
        {
            output["skip"] = 0;
        }

        return output;
    }

    private static bool SamePins(JsonNode? a, JsonNode? b) =>
        (a as JsonArray ?? []).Select(Int).SequenceEqual((b as JsonArray ?? []).Select(Int));

    private static JsonObject? Led(JsonObject cfg) => (cfg["hw"] as JsonObject)?["led"] as JsonObject;

    private static List<JsonObject> Outputs(JsonObject cfg) => [.. (Led(cfg)?["ins"] as JsonArray ?? []).OfType<JsonObject>()];

    // parsed numbers read as double, numbers set in code as int
    private static int? Int(JsonNode? node) => node is not JsonValue v ? null : v.TryGetValue(out int i) ? i : v.TryGetValue(out double d) ? (int)d : null;

    private static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    private static int? OptionalInt(JsonObject o, string name) => o[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out int i) => i,
        JsonValue v when v.TryGetValue(out double d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue => (int)d,
        _ => throw new HardwareSetupException($"{name} must be a whole number"),
    };

    // The board's address: a name on the local network, resolved, and an address the setup may contact.
    private async Task<Uri> RootAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        var (name, port) = DeviceConfig.SplitHostPort(host);
        if (name.Length == 0)
        {
            throw new HardwareSetupException("host is required");
        }

        if (!WledHosts.IsHostName(name) || port is <= 0 or > 65535)
        {
            throw new HardwareSetupException($"{host} isn’t an address: use the board’s IP address or its name");
        }

        IPAddress address;
        try
        {
            address = await _context.ResolveHost(name, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new HardwareSetupException($"Couldn’t find {host} on your network", ex);
        }

        if (!_allowAddress(address))
        {
            throw new HardwareSetupException($"{host} isn’t on your local network: only boards there can be set up");
        }

        var authority = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{authority}:{port ?? 80}/"));
    }

    // A request whose failure to connect says only that the board can't be reached (no socket details).
    private static async Task<RestReply> SendAsync(string host, Func<Task<RestReply>> request)
    {
        try
        {
            return await request();
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            throw new HardwareSetupException($"Couldn’t reach {host}", ex);
        }
    }

    private async Task<JsonObject> GetObjectAsync(string host, Uri root, string path, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(host, () => _rest.GetAsync(new Uri(root, path), cancellationToken));
        return reply is { IsSuccess: true, Body: JsonObject body }
            ? body
            : throw new HardwareSetupException($"The board at {host} isn’t a WLED board, or too old (/{path}: HTTP {reply.Status})");
    }

    private async Task PostStateAsync(string host, Uri root, JsonObject state, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(host, () => _rest.PostAsync(new Uri(root, "json/state"), state.ToJsonString(), cancellationToken));
        if (!reply.IsSuccess)
        {
            throw new HardwareSetupException($"The board didn’t take the colors (HTTP {reply.Status})");
        }
    }

    // WLED applies new LED settings in its main loop by building its outputs again, with every LED black: a check lit
    // before that is wiped (the strip stays dark). So after a write without a restart: wait until the board reports the
    // new LED count (at most 5 s), then a moment more.
    private async Task WaitUntilAppliedAsync(string host, Uri root, JsonObject hw, CancellationToken cancellationToken)
    {
        var outputs = ((hw["led"] as JsonObject)?["ins"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var total = outputs.Select(o => (Int(o["start"]) ?? 0) + (Int(o["len"]) ?? 0)).DefaultIfEmpty(0).Max();
        var deadline = _context.Time.GetUtcNow() + TimeSpan.FromSeconds(5);
        while (_context.Time.GetUtcNow() < deadline)
        {
            var info = await GetObjectAsync(host, root, "json/info", cancellationToken);
            if (Int((info["leds"] as JsonObject)?["count"]) == total)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), _context.Time, cancellationToken);
        }

        if (_applyWait > TimeSpan.Zero)
        {
            await Task.Delay(_applyWait, _context.Time, cancellationToken);
        }
    }

    // WLED saves the settings in its main loop: the restart follows a moment later. The board may already be on its
    // way down, so a failed request is fine.
    private async Task RestartAsync(Uri root, CancellationToken cancellationToken)
    {
        await Task.Delay(_restartDelay, _context.Time, cancellationToken);
        try
        {
            await _rest.PostAsync(new Uri(root, "json/state"), """{"rb":true}""", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            LogRestartUnanswered(root.Authority, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WLED setup {Host}: wrote {Sections} (restart: {Restart})")]
    private partial void LogWritten(string host, string sections, bool restart);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WLED setup {Host}: no answer to the restart request ({Reason}); it may be restarting already")]
    private partial void LogRestartUnanswered(string host, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WLED setup {Host}: check {Pattern} on output {Output}")]
    private partial void LogTest(string host, int output, WledTestPattern pattern);

    [LoggerMessage(Level = LogLevel.Information, Message = "WLED setup {Host}: looking for strips, round {Round}: a test output on each of the pins {Pins}")]
    private partial void LogProbe(string host, int round, string pins);

    [LoggerMessage(Level = LogLevel.Information, Message = "WLED setup {Host}: put the LED settings from before looking for strips back")]
    private partial void LogProbeRestored(string host);

    [LoggerMessage(Level = LogLevel.Information, Message = "WLED setup {Host}: restored the state from before the checks")]
    private partial void LogRestored(string host);

    private sealed record SavedState(JsonObject State, DateTimeOffset At);
}
