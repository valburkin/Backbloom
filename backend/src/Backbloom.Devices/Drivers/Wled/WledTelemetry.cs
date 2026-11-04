using System.Text.Json.Nodes;

namespace Backbloom.Devices.Drivers.Wled;

/// <summary>
/// The power limiter settings of a WLED controller from <c>GET /json/cfg</c> (WLED's cfg.cpp and bus_manager.cpp): the
/// global limit <c>hw.led.maxpwr</c>, each output's <c>hw.led.ins[].maxpwr</c> and LED current <c>ledma</c> (0: not
/// limited; 255: WS2815, 12 mA), and "force max brightness" for realtime data, <c>if.live.maxbri</c>. Which of them WLED
/// enforces depends on its version:
/// <list type="bullet">
/// <item>0.14 and older (no <c>ledma</c> per output): the global limit with the global LED current; a limit below 150 mA meant none.</item>
/// <item>0.15: only the outputs' own limits (<c>BusDigital::estimateCurrentAndLimitBri</c>); an output is limited with an
/// LED current and a limit of at least its share of the 100 mA WLED keeps for the ESP. The global value only seeds an
/// output that comes without a limit.</item>
/// <item>0.16 and the development branch (<c>BusManager::initializeABL</c>): the global limit when set and an output has an
/// LED current, else the outputs' own limits.</item>
/// </list>
/// With an unknown version the limiter counts as in use only when both 0.15's and the newer rules say so.
/// </summary>
/// <param name="LimiterInUse">WLED estimates and limits the current (its <c>pwr</c> reading means something).</param>
/// <param name="PowerLimitMilliamps">The limit WLED enforces (global, or the sum of the limited outputs'); null when none is.</param>
/// <param name="MilliampsPerLed">The outputs' LED current averaged over their LEDs; null when none is set.</param>
/// <param name="Groups">
/// The LEDs the limiter holds to a budget (<see cref="PowerGroup"/>, see <see cref="ReadGroups"/>); empty when unknown.
/// </param>
internal readonly record struct WledConfig(bool LimiterInUse, int? PowerLimitMilliamps, double? MilliampsPerLed, bool? ForceMaxBrightness, IReadOnlyList<PowerGroup>? Groups = null)
{
    // WLED's digital RGB bus types (const.h: WS281x, GS8608, WS2811 400 kHz, TM1829, UCS8903, APA106, and the two-wire
    // WS2801, APA102, LPD8806, P9813, LPD6803). Types with a white channel run through WLED's auto-white first and
    // count 3/4 of their channels, which Backbloom doesn't model: their groups are left to WLED.
    private static readonly HashSet<int> s_rgbTypes = [22, 23, 24, 25, 26, 27, 50, 51, 52, 53, 54];

    private const int Ws2815Marker = 255;
    private const int Ws2815Milliamps = 12;
    private const int OldMinimumLimit = 150;

    /// <summary>WLED's MA_FOR_ESP: the current it reserves for the ESP, split over its outputs (its rule for a limit that counts).</summary>
    internal const int EspMilliamps = 100;

    /// <summary>
    /// The ESP's current the budgets leave out: the most WLED reserves (120 mA on an ESP32, 80 on an ESP8266, 100 in
    /// older versions), so Backbloom's budget is never above WLED's.
    /// </summary>
    internal const int EspBudgetMilliamps = 120;

    /// <param name="version">WLED's <c>info.ver</c> ("0.15.0", "0.16.0-alpha"); null when unknown.</param>
    public static WledConfig Read(JsonObject cfg, string? version)
    {
        var led = (cfg["hw"] as JsonObject)?["led"] as JsonObject;
        var outputs = (led?["ins"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var global = Int(led?["maxpwr"]) ?? 0;
        var force = ((cfg["if"] as JsonObject)?["live"] as JsonObject)?["maxbri"] is JsonValue v && v.TryGetValue(out bool b) ? b : (bool?)null;

        // WLED 0.14 and older: one LED current for all outputs, and a limit below 150 mA meant none
        if (!outputs.Any(o => o.ContainsKey("ledma")))
        {
            var ma = Int(led?["ledma"]) ?? 0;
            var used = ma > 0 && global >= OldMinimumLimit;
            return new WledConfig(used, used ? global : null, ma > 0 ? (ma == Ws2815Marker ? Ws2815Milliamps : ma) : null, force, ReadGroups(cfg, version));
        }

        var withCurrent = outputs.Where(o => Int(o["ledma"]) > 0).ToList();
        var limited = withCurrent.Where(o => OwnLimitCounts(o, outputs.Count)).ToList();
        var perOutputLimit = limited.Count > 0 ? limited.Sum(o => Int(o["maxpwr"])!.Value) : (int?)null;

        // 0.15: the outputs' own limits only
        var v015 = (InUse: limited.Count > 0, Limit: perOutputLimit);

        // 0.16 / development branch: the global limit, else the outputs' own
        var newer = global > 0 ? (InUse: withCurrent.Count > 0, Limit: withCurrent.Count > 0 ? global : (int?)null) : v015;

        var (inUse, limit) = Generation(version) switch
        {
            WledGeneration.PerOutput => v015,
            WledGeneration.Newer => newer,
            _ => (v015.InUse && newer.InUse, v015.InUse && newer.InUse ? Math.Min(v015.Limit!.Value, newer.Limit!.Value) : (int?)null),
        };

        double weighted = 0;
        var leds = 0;
        foreach (var output in outputs)
        {
            if (Int(output["ledma"]) is { } ma and > 0 && Int(output["len"]) is { } length and > 0)
            {
                weighted += (ma == Ws2815Marker ? Ws2815Milliamps : ma) * (double)length;
                leds += length;
            }
        }

        return new WledConfig(inUse, limit, leds > 0 ? Math.Round(weighted / leds, 1) : null, force, ReadGroups(cfg, version));
    }

    /// <summary>
    /// The groups WLED's limiter holds to a budget (WLED's bus_manager.cpp, its behavior restated, no code): 0.14 and
    /// older one group of every output with the global limit; 0.15 each limited output alone, with its own limit minus
    /// its share (over all outputs) of the current kept for the ESP (<c>BusDigital::estimateCurrentAndLimitBri</c>);
    /// 0.16 and newer the outputs with an LED current together under a global limit, or without one, each limited
    /// output alone with its share over the limited outputs (<c>BusManager::initializeABL</c>). The ESP's current is
    /// <see cref="EspBudgetMilliamps"/>. Every budget also leaves 1 mA per LED at rest (for a global limit, every LED of
    /// the controller). An unknown version, a budget that is used up, or an output with a white channel or an unknown
    /// type in a group leaves that group to WLED (no group).
    /// </summary>
    internal static IReadOnlyList<PowerGroup> ReadGroups(JsonObject cfg, string? version)
    {
        var led = (cfg["hw"] as JsonObject)?["led"] as JsonObject;
        var outputs = (led?["ins"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var global = Int(led?["maxpwr"]) ?? 0;
        var allLeds = outputs.Sum(o => Math.Max(0, Int(o["len"]) ?? 0));
        var groups = new List<PowerGroup>();

        void Add(int budget, IEnumerable<(JsonObject Output, int LedCurrent)> members)
        {
            var list = members.ToList();
            if (budget <= 0 || list.Count == 0 || list.Any(m => !s_rgbTypes.Contains(Int(m.Output["type"]) ?? -1) || (Int(m.Output["len"]) ?? 0) <= 0))
            {
                return;
            }

            groups.Add(new PowerGroup(budget, [.. list.Select(m => new PowerRange(
                Math.Max(0, Int(m.Output["start"]) ?? 0),
                Int(m.Output["len"])!.Value,
                m.LedCurrent == Ws2815Marker ? Ws2815Milliamps : m.LedCurrent,
                m.LedCurrent == Ws2815Marker))]));
        }

        if (!outputs.Any(o => o.ContainsKey("ledma")))
        {
            // 0.14 and older: one LED current for every output
            var ma = Int(led?["ledma"]) ?? 0;
            if (ma > 0 && global >= OldMinimumLimit)
            {
                Add(global - EspBudgetMilliamps - allLeds, outputs.Select(o => (o, ma)));
            }

            return groups;
        }

        var generation = Generation(version);
        if (generation == WledGeneration.Newer && global > 0)
        {
            var withCurrent = outputs.Where(o => Int(o["ledma"]) > 0).ToList();
            Add(global - EspBudgetMilliamps - allLeds, withCurrent.Select(o => (o, Int(o["ledma"])!.Value)));
        }
        else if (generation is WledGeneration.Newer or WledGeneration.PerOutput)
        {
            var limited = outputs.Where(o => OwnLimitCounts(o, outputs.Count)).ToList();
            var sharing = generation == WledGeneration.Newer ? limited.Count : outputs.Count;
            foreach (var output in limited)
            {
                var budget = Int(output["maxpwr"])!.Value - (EspBudgetMilliamps / Math.Max(1, sharing)) - (Int(output["len"]) ?? 0);
                Add(budget, [(output, Int(output["ledma"])!.Value)]);
            }
        }

        return groups;
    }

    /// <summary>
    /// Whether output <paramref name="index"/> itself is limited, by the rules of <see cref="Read"/>: on 0.15 by its own
    /// limit (another output's doesn't help it), on 0.16 and newer by the global limit or its own, on 0.14 by the global
    /// one; with an unknown version only when both 0.15's and the newer rules say so.
    /// </summary>
    public static bool OutputLimited(JsonObject cfg, string? version, int index)
    {
        var led = (cfg["hw"] as JsonObject)?["led"] as JsonObject;
        var outputs = (led?["ins"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (index < 0 || index >= outputs.Count)
        {
            return false;
        }

        if (!outputs.Any(o => o.ContainsKey("ledma")))
        {
            return Read(cfg, version).LimiterInUse;
        }

        var output = outputs[index];
        var global = Int(led?["maxpwr"]) ?? 0;
        var hasCurrent = Int(output["ledma"]) > 0;
        var own = hasCurrent && OwnLimitCounts(output, outputs.Count);
        var newer = global > 0 ? hasCurrent : own;
        return Generation(version) switch
        {
            WledGeneration.PerOutput => own,
            WledGeneration.Newer => newer,
            _ => own && newer,
        };
    }

    // 0.15 skips an output whose limit is below its share of the ESP's current (BusDigital::estimateCurrentAndLimitBri)
    private static bool OwnLimitCounts(JsonObject output, int outputs) =>
        Int(output["ledma"]) > 0 && Int(output["maxpwr"]) is { } max && max > 0 && max >= EspMilliamps / Math.Max(1, outputs);

    private enum WledGeneration
    {
        Unknown,
        PerOutput,
        Newer,
    }

    // "0.15.0", "0.15.0-b3" -> 0.15; anything from 0.16 on (and 1.x) is newer
    private static WledGeneration Generation(string? version)
    {
        var parts = (version ?? string.Empty).Split('.', '-', ' ');
        if (parts.Length < 2 || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor))
        {
            return WledGeneration.Unknown;
        }

        return (major, minor) switch
        {
            (0, < 15) => WledGeneration.Unknown,
            (0, 15) => WledGeneration.PerOutput,
            _ => WledGeneration.Newer,
        };
    }

    private static int? Int(JsonNode? node) => node is not JsonValue v ? null : v.TryGetValue(out int i) ? i : v.TryGetValue(out double d) ? (int)d : null;
}
/// <summary>
/// A temperature from a WLED usermod: entries of <c>info.sensor</c> and <c>info.u</c> with "temp" in the name shaped
/// <c>[value, "°C"]</c> or <c>"°F"</c>. The Temperature usermod reports a missing or failed sensor as
/// <c>[0, " Sensor Error!"]</c> (no degree sign: skipped); the Internal Temperature usermod reports -1 where the chip has
/// no sensor. "Internal" names the chip's own temperature; a sensor elsewhere (at the strip or the power supply) is
/// preferred, as it says more about overheating.
/// </summary>
internal sealed record WledTemperature(double Celsius, string Sensor, bool IsChip)
{
    private const double NoReading = -1;
    private const double Lowest = -100;

    public static WledTemperature? Read(JsonObject? info)
    {
        var found = Readings(info?["sensor"] as JsonObject).Concat(Readings(info?["u"] as JsonObject)).ToList();
        return found.FirstOrDefault(t => !t.IsChip) ?? found.FirstOrDefault();
    }

    private static IEnumerable<WledTemperature> Readings(JsonObject? entries)
    {
        foreach (var (name, value) in entries ?? [])
        {
            if (!name.Contains("temp", StringComparison.OrdinalIgnoreCase)
                || value is not JsonArray { Count: > 1 } entry
                || entry[0] is not JsonValue reading
                || !reading.TryGetValue(out double degrees)
                || entry[1] is not JsonValue unitValue
                || !unitValue.TryGetValue(out string? unit)
                || !unit.Contains('°', StringComparison.Ordinal))
            {
                continue;
            }

            var chip = name.Contains("internal", StringComparison.OrdinalIgnoreCase);
            if (degrees <= Lowest || (chip && degrees == NoReading))
            {
                continue;
            }

            var celsius = unit.Contains('F', StringComparison.Ordinal) ? (degrees - 32) * 5 / 9 : degrees;
            yield return new WledTemperature(Math.Round(celsius, 1), name, chip);
        }
    }
}
