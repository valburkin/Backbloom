using System.Text.Json.Nodes;

namespace Backbloom.Devices.Setup;

/// <summary>
/// The pins finding the strips tries, per chip (from WLED's pin rules: never the flash or PSRAM pins, the USB pins,
/// input-only pins, the boot strapping pins the board needs low or high at power-on, or the serial console's TX), in
/// two rounds: first the pins strips are wired to most often, at most as many as the chip has RMT channels, then the
/// remaining safe ones (at most as many outputs as WLED drives on that chip). GPIO 3 (the ESP32's serial RX) is in
/// round 2: QuinLED-style boards wire their second LED output there.
/// </summary>
public static class WledPins
{
    private static readonly Dictionary<string, (int[] Round1, int[] Round2)> s_pins = new(StringComparer.Ordinal)
    {
        // RMT 8; outputs 16. Not 0 (boot), 1 (TX), 6-11 (flash), 12 (flash voltage strap), 34-39 (input only)
        ["esp32"] = ([16, 2, 4, 5, 17, 18, 19, 21], [3, 13, 14, 15, 22, 23, 25, 26, 27, 32, 33]),

        // outputs 3 (GPIO 3 by DMA, 2 by UART1, the others bit-banged). Not 0 (boot), 1 (TX), 6-11 (flash), 15 (boot), 16
        ["esp8266"] = ([2, 3], [5, 4, 14]),

        // RMT 4; outputs 12. Not 0 (boot), 19-20 (USB), 26-32 (flash, PSRAM), 43-44 (console), 45-46 (straps, input)
        ["esp32-s2"] = ([16, 18, 2, 4], [1, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 33]),

        // RMT 4; outputs 12. Not 0 and 3 (straps), 19-20 (USB), 26-37 (flash, octal PSRAM), 43-44 (console), 45-46 (straps)
        ["esp32-s3"] = ([16, 18, 2, 4], [1, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]),

        // RMT 2; outputs 2. Not 8 and 9 (straps), 11-17 (flash), 18-19 (USB), 20-21 (console)
        ["esp32-c3"] = ([2, 10], [4, 5]),
    };

    /// <summary>The chip family of WLED's <c>info.arch</c> ("ESP32-D0WD-V3" → "esp32", "ESP32-S3" → "esp32-s3", "esp8266").</summary>
    public static string Family(string? arch)
    {
        var a = (arch ?? string.Empty).ToUpperInvariant();
        return a.Contains("8266", StringComparison.Ordinal) ? "esp8266"
            : a.Contains("S3", StringComparison.Ordinal) ? "esp32-s3"
            : a.Contains("S2", StringComparison.Ordinal) ? "esp32-s2"
            : a.Contains("C3", StringComparison.Ordinal) ? "esp32-c3"
            : "esp32";
    }

    /// <summary>The pins of a round (1 or 2) for a chip; an unknown chip as an ESP32.</summary>
    public static IReadOnlyList<int> Candidates(string? arch, int round)
    {
        var (first, second) = s_pins[Family(arch)];
        return round == 1 ? first : second;
    }

    /// <summary>
    /// How many test outputs a probe may write at most: WLED before 0.15 drives fewer digital outputs on an ESP32 (its
    /// 8 RMT channels; 0.15 and newer up to 16), and outputs past its limit are dropped without an error. Other chips:
    /// no cap beyond their rounds.
    /// </summary>
    public static int MaxOutputs(string? arch, string? version)
    {
        var parts = (version ?? string.Empty).Split('.', '-', 'b');
        var old = parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) && major == 0 && minor < 15;
        return old && Family(arch) == "esp32" ? 8 : int.MaxValue;
    }

    /// <summary>The pins a board's settings give to something other than LEDs: buttons, relay, IR, I2C, SPI.</summary>
    public static IReadOnlySet<int> UsedPins(JsonObject cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var hw = cfg["hw"] as JsonObject;
        var used = new HashSet<int>();
        foreach (var button in ((hw?["btn"] as JsonObject)?["ins"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Int(button["type"]) is > 0)
            {
                AddAll(used, button["pin"]);
            }
        }

        AddAll(used, (hw?["ir"] as JsonObject)?["pin"]);
        AddAll(used, (hw?["relay"] as JsonObject)?["pin"]);
        var interfaces = hw?["if"] as JsonObject;
        AddAll(used, interfaces?["i2c-pin"]);
        AddAll(used, interfaces?["spi-pin"]);
        return used;
    }

    private static void AddAll(HashSet<int> used, JsonNode? pins)
    {
        foreach (var node in pins is JsonArray list ? [.. list] : new[] { pins })
        {
            if (Int(node) is { } pin and >= 0)
            {
                used.Add(pin);
            }
        }
    }

    private static int? Int(JsonNode? node) => node is not JsonValue v ? null : v.TryGetValue(out int i) ? i : v.TryGetValue(out double d) ? (int)d : null;
}
