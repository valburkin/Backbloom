using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Backbloom.Discovery.Ssdp;

namespace Backbloom.Devices.Drivers;

/// <summary>
/// SSDP discovery for the drivers, with the C++ result shape (port of <c>SSDPDiscover::discoverServices</c> /
/// <c>getServicesDiscoveredJson</c>): <c>{ id, cache-control, location, server, st, usn, ip, port, hostname, other }</c>,
/// plus <c>value</c> (the address to configure) and <c>name</c> for the UI.
/// </summary>
public static class SsdpDevices
{
    private static readonly string[] s_known = ["CACHE-CONTROL", "LOCATION", "SERVER", "ST", "USN"];

    /// <param name="filterHeader">Header the <paramref name="filter"/> regular expression is matched against.</param>
    /// <param name="skipDuplicates">Keep one answer per LOCATION (the last one).</param>
    public static async Task<JsonArray> SearchAsync(
        DeviceRequest request, string searchTarget, int port, string filterHeader, string filter, bool skipDuplicates, Func<string, string, string> name)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(name);
        var regex = new Regex(filter, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var responses = await request.Context.Discovery.Ssdp.SearchAsync(searchTarget, port, TimeSpan.FromSeconds(3), request.CancellationToken);
        var found = new List<(string Key, SsdpResponse Response)>();
        foreach (var r in responses)
        {
            if (!regex.IsMatch(r.Header(filterHeader)))
            {
                continue;
            }

            var key = r.Location;
            var existing = found.FindIndex(f => f.Key == key);
            if (skipDuplicates && existing >= 0)
            {
                found[existing] = (key, r);
            }
            else
            {
                found.Add((key, r));
            }
        }

        var list = new JsonArray();
        foreach (var (key, r) in found.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            list.Add((JsonNode)ToJson(key, r, name));
        }

        return list;
    }

    public static JsonObject ToJson(string key, SsdpResponse response, Func<string, string, string> name)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(name);
        var location = response.Location;
        string ip = response.From.Address.ToString();
        var port = -1;
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri))
        {
            ip = uri.Host;
            port = uri.IsDefaultPort ? -1 : uri.Port;
        }

        var other = new JsonObject();
        foreach (var (header, value) in response.Headers.Where(h => !s_known.Contains(h.Key)).OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            other[header.ToLowerInvariant()] = value;
        }

        var address = port > 0 ? string.Create(CultureInfo.InvariantCulture, $"{ip}:{port}") : ip;
        return new JsonObject
        {
            ["id"] = key,
            ["cache-control"] = response.Header("CACHE-CONTROL"),
            ["location"] = location,
            ["server"] = response.Header("SERVER"),
            ["st"] = response.Header("ST"),
            ["usn"] = response.Header("USN"),
            ["ip"] = ip,
            ["port"] = port,
            ["hostname"] = ip,
            ["other"] = other,
            ["value"] = address,
            ["name"] = name(ip, address),
        };
    }
}
