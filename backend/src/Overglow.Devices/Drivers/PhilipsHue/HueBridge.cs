using System.Globalization;
using System.Text.Json.Nodes;

namespace Overglow.Devices.Drivers.PhilipsHue;

/// <summary>
/// REST access to a Hue bridge (port of <c>LedDevicePhilipsHueBridge</c>): API v1 under <c>/api/&lt;username&gt;/</c>
/// (http, or https when the address says so), API v2 under <c>/clip/v2/resource</c> over https with the
/// <c>hue-application-key</c> header. Bridge errors (other than 901) are reported as <see cref="HueApiException"/>.
/// </summary>
public sealed class HueBridge
{
    private readonly JsonRest _rest;

    public HueBridge(string address, string username, bool apiV2, DriverContext context)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(context);
        var (scheme, host, port) = ParseAddress(address);
        ApiV2 = apiV2;
        Username = username;
        Host = host;
        if (apiV2)
        {
            Root = new Uri($"https://{DeviceConfig.UrlHost(host)}:443/");
            _rest = new JsonRest(context.LanHttps, new Dictionary<string, string> { ["hue-application-key"] = username });
        }
        else
        {
            Root = new Uri(string.Create(CultureInfo.InvariantCulture, $"{scheme}://{DeviceConfig.UrlHost(host)}:{port}/"));
            _rest = new JsonRest(scheme == "https" ? context.LanHttps : context.Http);
        }
    }

    public bool ApiV2 { get; }

    public string Username { get; }

    public string Host { get; }

    public Uri Root { get; }

    /// <summary>QUrl::fromUserInput: "1.2.3.4", "1.2.3.4:8080" or "https://1.2.3.4" (default port by scheme).</summary>
    public static (string Scheme, string Host, int Port) ParseAddress(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var text = address.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        var uri = new Uri(text);
        return (uri.Scheme, uri.Host.Trim('[', ']'), uri.Port);
    }

    /// <summary>A v1 resource ("lights/3/state") or an absolute v2 path ("/clip/v2/resource/light/...").</summary>
    public Uri Resource(string route) =>
        route.StartsWith('/') ? new Uri(Root, route) : new Uri(Root, $"api/{Username}/{route}");

    public async Task<JsonNode?> GetAsync(string route, CancellationToken cancellationToken) =>
        Check((await _rest.GetAsync(Resource(route), cancellationToken)).Body, false);

    public async Task<RestReply> GetRawAsync(string route, CancellationToken cancellationToken) => await _rest.GetAsync(Resource(route), cancellationToken);

    /// <summary>PUT (the C++ "post" helper sends PUT requests).</summary>
    public async Task<JsonNode?> PutAsync(string route, string body, bool suppressError, CancellationToken cancellationToken) =>
        Check((await _rest.PutAsync(Resource(route), body, cancellationToken)).Body, suppressError);

    /// <summary>
    /// Pairs with the bridge (the link button must have been pressed): POST /api with a device type and
    /// <c>generateclientkey</c>, returning the bridge's answer.
    /// </summary>
    public static async Task<JsonObject> PairAsync(string address, DriverContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (scheme, host, port) = ParseAddress(address);
        var uri = new Uri(string.Create(CultureInfo.InvariantCulture, $"{scheme}://{DeviceConfig.UrlHost(host)}:{port}/api"));
        var body = new JsonObject { ["devicetype"] = $"overglow#{Environment.MachineName}", ["generateclientkey"] = true };
        var reply = await new JsonRest(scheme == "https" ? context.LanHttps : context.Http).PostAsync(uri, body.ToJsonString(LampColor.QtJson), cancellationToken);
        var first = (reply.Body as JsonArray)?.FirstOrDefault() as JsonObject;
        if (first?["success"] is JsonObject success)
        {
            return new JsonObject { ["username"] = success["username"]?.DeepClone(), ["clientkey"] = success["clientkey"]?.DeepClone() };
        }

        var error = first?["error"] as JsonObject;
        return new JsonObject { ["error"] = error is null ? $"Unexpected answer (HTTP {reply.Status})" : DeviceConfig.GetString(error, "description") };
    }

    /// <summary>Port of <c>checkApiError</c>: an error in the first array element (type 901 is ignored).</summary>
    private static JsonNode? Check(JsonNode? body, bool suppressError)
    {
        if (body is JsonArray { Count: > 0 } list && list[0] is JsonObject first && first["error"] is JsonObject error)
        {
            var type = error["type"]?.ToString() ?? string.Empty;
            if (type != "901" && !suppressError)
            {
                throw new HueApiException($"({type}) {DeviceConfig.GetString(error, "description")}, Resource:{DeviceConfig.GetString(error, "address")}");
            }
        }

        return body;
    }
}

/// <summary>The bridge answered a request with an error.</summary>
public sealed class HueApiException : IOException
{
    public HueApiException()
    {
    }

    public HueApiException(string message)
        : base(message)
    {
    }

    public HueApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
