using System.Text.Json.Nodes;

namespace Backbloom.Devices.Drivers.Wled;

/// <summary>
/// The power limiter settings of a WLED controller from <c>GET /json/cfg</c> (WLED's cfg.cpp): the global limit
/// <c>hw.led.maxpwr</c> (0: limits per output), each output's <c>hw.led.ins[].maxpwr</c> and LED current
/// <c>ledma</c> (0: not limited; 255: WS2815, 12 mA), and "force max brightness" for realtime data,
/// <c>if.live.maxbri</c>. As WLED's <c>BusManager::initializeABL</c>: the limiter is in use with a global limit and an
/// output with an LED current, or without one, with an output that has both.
/// </summary>
/// <param name="LimiterInUse">WLED estimates and limits the current (its <c>pwr</c> reading means something).</param>
/// <param name="PowerLimitMilliamps">The global limit, else the sum of the limited outputs' limits; null when none is set.</param>
/// <param name="MilliampsPerLed">The outputs' LED current averaged over their LEDs; null when none is set.</param>
internal readonly record struct WledConfig(bool LimiterInUse, int? PowerLimitMilliamps, double? MilliampsPerLed, bool? ForceMaxBrightness)
{
    private const int Ws2815Marker = 255;
    private const int Ws2815Milliamps = 12;
    private const int OldMinimumLimit = 150;

    public static WledConfig Read(JsonObject cfg)
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
            return new WledConfig(used, used ? global : null, ma > 0 ? (ma == Ws2815Marker ? Ws2815Milliamps : ma) : null, force);
        }

        var limited = outputs.Where(o => Int(o["ledma"]) > 0 && Int(o["maxpwr"]) > 0).ToList();
        var anyCurrent = outputs.Any(o => Int(o["ledma"]) > 0);
        var inUse = (global > 0 && anyCurrent) || (global == 0 && limited.Count > 0);
        int? limit = !inUse ? null : global > 0 ? global : limited.Sum(o => Int(o["maxpwr"])!.Value);

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

        return new WledConfig(inUse, limit, leds > 0 ? Math.Round(weighted / leds, 1) : null, force);
    }

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue(out double d) ? (int)d : null;
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
