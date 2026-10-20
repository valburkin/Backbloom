using System.Globalization;
using System.Text.Json.Nodes;

namespace Backbloom.Devices;

/// <summary>Tolerant readers for the <c>device</c> settings object (values are validated by the settings schema).</summary>
public static class DeviceConfig
{
    public static string GetString(JsonObject config, string name, string fallback = "") =>
        config[name] is JsonValue v && v.TryGetValue(out string? s) ? s : fallback;

    public static int GetInt(JsonObject config, string name, int fallback) =>
        config[name] is JsonValue v && v.TryGetValue(out double d) ? (int)d : fallback;

    /// <summary>An integer that older configurations may store as a string ("1").</summary>
    public static int GetIntOrString(JsonObject config, string name, int fallback) =>
        config[name] is JsonValue v && v.TryGetValue(out string? s)
            ? int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback
            : GetInt(config, name, fallback);

    public static double GetDouble(JsonObject config, string name, double fallback) =>
        config[name] is JsonValue v && v.TryGetValue(out double d) ? d : fallback;

    public static bool GetBool(JsonObject config, string name, bool fallback) =>
        config[name] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    public static JsonArray GetArray(JsonObject config, string name) => config[name] as JsonArray ?? [];

    /// <summary>The objects of an array setting (other items are skipped).</summary>
    public static IEnumerable<JsonObject> GetObjects(JsonObject config, string name) => GetArray(config, name).OfType<JsonObject>();

    /// <summary>Splits "host" or "host:port" (IPv6 literals in brackets).</summary>
    public static (string Host, int? Port) SplitHostPort(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var a = address.Trim();
        if (a.StartsWith('['))
        {
            var end = a.IndexOf(']', StringComparison.Ordinal);
            if (end > 0)
            {
                var host = a[1..end];
                return end + 2 < a.Length && a[end + 1] == ':' && int.TryParse(a[(end + 2)..], out var p6) ? (host, p6) : (host, null);
            }
        }

        var colon = a.LastIndexOf(':');
        if (colon > 0 && a.IndexOf(':', StringComparison.Ordinal) == colon && int.TryParse(a[(colon + 1)..], out var port))
        {
            return (a[..colon], port);
        }

        return (a, null);
    }

    /// <summary>Formats a host for a URL authority (IPv6 literals get brackets).</summary>
    public static string UrlHost(string host) =>
        System.Net.IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{host}]" : host;
}
