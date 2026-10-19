using System.Text.Json.Nodes;

namespace Overglow.Devices;

/// <summary>Tolerant readers for the <c>device</c> settings object (values are validated by the settings schema).</summary>
public static class DeviceConfig
{
    public static string GetString(JsonObject config, string name, string fallback = "") =>
        config[name] is JsonValue v && v.TryGetValue(out string? s) ? s : fallback;

    public static int GetInt(JsonObject config, string name, int fallback) =>
        config[name] is JsonValue v && v.TryGetValue(out double d) ? (int)d : fallback;

    public static bool GetBool(JsonObject config, string name, bool fallback) =>
        config[name] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    /// <summary>Splits "host" or "host:port" (IPv6 literals in brackets).</summary>
    public static (string Host, int? Port) SplitHostPort(string address)
    {
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
}
