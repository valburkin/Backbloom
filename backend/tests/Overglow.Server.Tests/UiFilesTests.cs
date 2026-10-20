using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Overglow.App;

namespace Overglow.Server.Tests;

/// <summary>The built UI served by the backend: files, the single-page fallback and the cache headers.</summary>
public sealed class UiFilesTests : IAsyncLifetime
{
    private const string Index = "<!doctype html><title>Overglow</title><script src=\"./assets/index-AbC123.js\"></script>";
    private readonly string _dir = TestServer.NewDataDirectory();
    private Microsoft.AspNetCore.Builder.WebApplication? _app;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        var ui = Path.Combine(_dir, "ui");
        Directory.CreateDirectory(Path.Combine(ui, "assets"));
        await File.WriteAllTextAsync(Path.Combine(ui, "index.html"), Index, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(ui, "assets", "index-AbC123.js"), "console.log(1)", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(ui, "manifest.webmanifest"), "{}", TestContext.Current.CancellationToken);
        _app = OverglowHost.Build(["--data-dir", _dir, "--legacy-data-dir", "none", "--bind", "127.0.0.1", "--port", "0", "--capture", "synthetic", "--ui-dir", ui]);
        await _app.StartAsync(TestContext.Current.CancellationToken);
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }

        TestServer.DeleteDirectory(_dir);
    }

    [Fact]
    public async Task Root_ServesIndex_RevalidatedOnEveryLoad()
    {
        using var response = await _http.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Index, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task HashedAssets_AreCachedForAYear()
    {
        using var response = await _http.GetAsync(new Uri("/assets/index-AbC123.js", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task OtherFiles_AreRevalidated_WithTheirType()
    {
        using var response = await _http.GetAsync(new Uri("/manifest.webmanifest", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/settings")]
    [InlineData("/devices/wled/1")]
    public async Task PathsWithoutAFile_GetTheApp(string path)
    {
        using var response = await _http.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal(Index, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var head = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, path), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
    }

    [Theory]
    [InlineData("/missing.js")]
    [InlineData("/assets/old-hash")]
    [InlineData("/assets/index-Old999.js")]
    public async Task MissingFiles_Are404(string path)
    {
        using var response = await _http.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheApi_StillAnswers()
    {
        using var content = new StringContent("""{ "command": "sysinfo", "tan": 4 }""");
        using var response = await _http.PostAsync(new Uri("/json-rpc", UriKind.Relative), content, TestContext.Current.CancellationToken);
        Assert.Contains("\"success\":true", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
