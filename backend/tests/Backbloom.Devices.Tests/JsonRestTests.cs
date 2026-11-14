using Backbloom.Devices.Drivers;

namespace Backbloom.Devices.Tests;

/// <summary>The REST helper's failures, which drivers write to the log and the UI shows.</summary>
public sealed class JsonRestTests
{
    private sealed class NoAnswer : HttpMessageHandler
    {
        // what HttpClient does when the helper's own timeout fires: the request is canceled
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("no answer");
    }

    [Theory]
    [InlineData("http://10.0.0.3:16021/api/v1/nanoleaf-token-1/state", "http://10.0.0.3:16021", "nanoleaf-token-1")]
    [InlineData("http://10.0.0.4:80/api/hue-user-key-1/lights/3/state", "http://10.0.0.4", "hue-user-key-1")]
    [InlineData("http://admin:secret-password@10.0.0.5:8080/state", "http://10.0.0.5:8080", "secret-password")]
    [InlineData("http://[fe80::1]:16021/api/v1/nanoleaf-token-2/state", "http://[fe80::1]:16021", "nanoleaf-token-2")]
    public async Task ATimeout_NamesTheAddress_ButNotThePathOrPasswordWithItsKey(string url, string address, string secret)
    {
        using var handler = new NoAnswer();
        using var http = new HttpClient(handler);
        var rest = new JsonRest(http);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => rest.PutAsync(new Uri(url), "{}", TestContext.Current.CancellationToken));
        Assert.Equal($"PUT {address} timed out.", ex.Message);
        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
    }
}
