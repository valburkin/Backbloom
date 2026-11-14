using Backbloom.Devices.Drivers;

namespace Backbloom.Devices.Tests;

/// <summary>The REST helper's failures, which drivers write to the log.</summary>
public sealed class JsonRestTests
{
    private sealed class NoAnswer : HttpMessageHandler
    {
        // what HttpClient does when the helper's own timeout fires: the request is canceled
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("no answer");
    }

    [Fact]
    public async Task ATimeout_NamesTheAddress_ButNotThePathWithItsKey()
    {
        using var handler = new NoAnswer();
        using var http = new HttpClient(handler);
        var rest = new JsonRest(http);
        var uri = new Uri("http://10.0.0.3:16021/api/v1/nanoleaf-token-1/state");

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => rest.PutAsync(uri, "{}", TestContext.Current.CancellationToken));
        Assert.Equal("PUT http://10.0.0.3:16021 timed out.", ex.Message);
        Assert.DoesNotContain("nanoleaf-token-1", ex.Message, StringComparison.Ordinal);
    }
}
