using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.App;
using Overglow.Devices;
using Overglow.Engine;
using Overglow.Settings;
using Overglow.Shell;

namespace Overglow.Shell.Tests;

/// <summary>The real host, headless, on a random port with a temporary data directory.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private TestHost(WebApplication app, string dataDirectory)
    {
        App = app;
        DataDirectory = dataDirectory;
    }

    public WebApplication App { get; }

    public string DataDirectory { get; }

    public LightingEngine Engine => App.Services.GetRequiredService<LightingEngine>();

    public ISettingsStore Settings => App.Services.GetRequiredService<ISettingsStore>();

    public static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"overglow-shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Starts a host; <paramref name="settings"/> (sections as JSON) are stored first.</summary>
    public static async Task<TestHost> StartAsync(string? settings = null, Action<AppOptions, IServiceCollection>? configure = null)
    {
        var dir = NewDirectory();
        if (settings is not null)
        {
            using var services = SettingsServices.Open(new SettingsOptions(dir, null), Schema(), NullLoggerFactory.Instance);
            var result = services.Store.Update(JsonNode.Parse(settings)!.AsObject());
            Assert.True(result.Success, string.Join("; ", result.Errors));
        }

        var app = OverglowHost.Build(["--data-dir", dir, "--legacy-data-dir", "none", "--port", "0", "--capture", "synthetic", "--ui-dir", Path.Combine(dir, "no-ui")], configure);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new TestHost(app, dir);
    }

    public static SettingsSchema Schema() =>
        SettingsSchema.CreateBuiltin(new DriverRegistry().RegisterAll().Drivers.ToDictionary(d => d.Type, d => d.Schema, StringComparer.Ordinal));

    public static void DeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // a device thread may still hold a file for a moment
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
        DeleteDirectory(DataDirectory);
    }
}

internal static class Wait
{
    public static void Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            Thread.Sleep(10);
        }
    }
}

internal sealed class FakeWindow : IAppWindow
{
    public int Shown { get; private set; }

    public void Show() => Shown++;

    public void Dispose()
    {
    }
}

internal sealed class FakeAutostart : IAutostart
{
    public string Label => "Start with the OS";

    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;
}

internal sealed class FakeSystemEvents : ISystemEvents
{
    public event Action<SystemEvent>? Raised;

    public void Raise(SystemEvent e) => Raised?.Invoke(e);
}
