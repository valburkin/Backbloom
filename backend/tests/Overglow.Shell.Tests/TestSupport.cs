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

    /// <summary>The default startup effect, switched off so tests start with the screen visible.</summary>
    public const string NoStartupEffect = """{ "enable": false, "type": "effect", "color": [255, 0, 0], "effect": "Rainbow swirl fast", "duration_ms": 3000 }""";

    /// <summary>
    /// Starts a host; <paramref name="settings"/> (sections as JSON) are stored first. The startup effect is off unless
    /// the settings contain <c>foregroundEffect</c>.
    /// </summary>
    public static async Task<TestHost> StartAsync(string? settings = null, Action<AppOptions, IServiceCollection>? configure = null)
    {
        var dir = NewDirectory();
        var sections = settings is null ? new JsonObject() : JsonNode.Parse(settings)!.AsObject();
        if (!sections.ContainsKey("foregroundEffect"))
        {
            sections["foregroundEffect"] = JsonNode.Parse(NoStartupEffect);
        }

        using (var services = SettingsServices.Open(new SettingsOptions(dir, null), Schema(), NullLoggerFactory.Instance))
        {
            var result = services.Store.Update(sections);
            Assert.True(result.Success, string.Join("; ", result.Errors));
        }

        var app = OverglowHost.Build(["--data-dir", dir, "--legacy-data-dir", "none", "--bind", "127.0.0.1", "--port", "0", "--capture", "synthetic", "--ui-dir", Path.Combine(dir, "no-ui")], configure);
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
    /// <summary>Waits until the visible source matches, woken by <see cref="PriorityManager.VisibleChanged"/>.</summary>
    public static VisibleSource ForVisible(LightingEngine engine, Func<VisibleSource, bool> condition, int timeoutMs = 5000)
    {
        using var changed = new SemaphoreSlim(0);
        void OnChanged(VisibleSource _) => changed.Release();
        engine.Sources.VisibleChanged += OnChanged;
        try
        {
            ForSignal(() => condition(engine.Sources.Visible), changed, timeoutMs, "visible source");
            return engine.Sources.Visible;
        }
        finally
        {
            engine.Sources.VisibleChanged -= OnChanged;
        }
    }

    /// <summary>Waits until the screen is the visible source (it becomes one with its first captured frame).</summary>
    public static void ForScreen(LightingEngine engine, int timeoutMs = 5000) =>
        ForVisible(engine, v => v.Component == Core.Priorities.SourceComponent.SystemGrabber, timeoutMs);

    /// <summary>Waits until <paramref name="component"/> is <paramref name="on"/>, woken by <see cref="ComponentStates.Changed"/>.</summary>
    public static void ForComponent(LightingEngine engine, Component component, bool on, int timeoutMs = 5000)
    {
        using var changed = new SemaphoreSlim(0);
        void OnChanged(Component c, bool _) => changed.Release();
        engine.Components.Changed += OnChanged;
        try
        {
            ForSignal(() => engine.Components.IsOn(component) == on, changed, timeoutMs, $"{component} {(on ? "on" : "off")}");
        }
        finally
        {
            engine.Components.Changed -= OnChanged;
        }
    }

    private static void ForSignal(Func<bool> condition, SemaphoreSlim changed, int timeoutMs, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            var left = timeoutMs - (int)sw.ElapsedMilliseconds;
            if (left <= 0 || !changed.Wait(left))
            {
                if (condition())
                {
                    return;
                }

                throw new TimeoutException($"Timed out waiting for {what}.");
            }
        }
    }

    /// <summary>Polls a state that raises no event (file contents).</summary>
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
