using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backbloom.Devices.Drivers;

/// <summary>The reply of a REST call: HTTP status, the parsed body (null when empty or not JSON) and the headers.</summary>
public sealed record RestReply(int Status, JsonNode? Body, HttpResponseHeaders Headers)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>
/// Small JSON-over-HTTP helper for the REST drivers (port of the parts of <c>ProviderRestApi</c> they use): one
/// request at a time with a timeout, optional extra headers, bodies sent as <c>application/json</c>.
/// </summary>
public sealed class JsonRest(HttpClient http, IReadOnlyDictionary<string, string>? headers = null)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public Task<RestReply> GetAsync(Uri uri, CancellationToken cancellationToken) => SendAsync(HttpMethod.Get, uri, null, cancellationToken);

    public Task<RestReply> PutAsync(Uri uri, string body, CancellationToken cancellationToken) => SendAsync(HttpMethod.Put, uri, body, cancellationToken);

    public Task<RestReply> PostAsync(Uri uri, string body, CancellationToken cancellationToken) => SendAsync(HttpMethod.Post, uri, body, cancellationToken);

    public async Task<RestReply> SendAsync(HttpMethod method, Uri uri, string? body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(method, uri);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            JsonNode? json = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    json = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                }
            }

            return new RestReply((int)response.StatusCode, json, response.Headers);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{method} {uri} timed out.");
        }
    }
}
