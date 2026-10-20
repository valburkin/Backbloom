using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Overglow.App;
using Overglow.Engine;
using Overglow.Engine.Platform;
using Overglow.Server.Rpc;
using Overglow.Settings;
using Overglow.Tests.Platform;

namespace Overglow.Shell.Tests;

/// <summary>
/// The real host with the file driver: nothing reaches the LED controller before "Start lighting" or while HyperHDR
/// runs, the startup effect waits for the start, and autostart stays off unless the user turns it on.
/// </summary>
public sealed class FirstRunTests
{
    private const string StartupEffect = """{ "enable": true, "type": "effect", "color": [255, 0, 0], "effect": "Rainbow swirl fast", "duration_ms": 1000 }""";

    private static string Settings(string output) => new JsonObject
    {
        ["device"] = new JsonObject { ["type"] = "file", ["output"] = output, ["printTimeStamp"] = false },
        ["foregroundEffect"] = JsonNode.Parse(StartupEffect),
    }.ToJsonString();

    private static string NewOutput() => Path.Combine(Path.GetTempPath(), $"overglow-firstrun-{Guid.NewGuid():N}.txt");

    private static long Written(string output) => File.Exists(output) ? new FileInfo(output).Length : 0;

    [Fact]
    public async Task FirstRun_SendsNothing_UntilTheLightingIsStarted_ThenRemembersIt()
    {
        var output = NewOutput();
        var host = await TestHost.StartAsync(Settings(output), firstRun: true);
        try
        {
            var setup = host.App.Services.GetRequiredService<SetupService>();
            Assert.False(State(setup)["firstRunCompleted"]!.GetValue<bool>());
            Assert.True(State(setup)["outputHeld"]!.GetValue<bool>());

            Thread.Sleep(500);
            Assert.Equal(0, Written(output));
            Assert.Empty(host.Engine.Effects.Active);
            Assert.True(host.Engine.Components.OutputHeld);

            setup.Start(autostart: null);
            Assert.True(State(setup)["firstRunCompleted"]!.GetValue<bool>());
            Assert.Equal("Rainbow swirl fast", Assert.Single(host.Engine.Effects.Active).Name);
            Wait.Until(() => Written(output) > 0);

            // the next start doesn't wait for the welcome again
            await host.App.StopAsync(TestContext.Current.CancellationToken);
            await host.App.DisposeAsync();
            await using var again = await RestartAsync(host.DataDirectory);
            Assert.False(again.Services.GetRequiredService<LightingEngine>().Components.OutputHeld);
            Assert.True(again.Services.GetRequiredService<ISetupState>().FirstRunCompleted);
        }
        finally
        {
            TestHost.DeleteDirectory(host.DataDirectory);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task HyperHdrRunning_SendsNothing_UntilItQuits()
    {
        var output = NewOutput();
        var processes = new FakeProcessList();
        processes.Start("hyperhdr.exe");
        var time = new FakeTimeProvider();
        try
        {
            await using var host = await TestHost.StartAsync(Settings(output), configure: (_, services) =>
                services.AddSingleton(new OtherAppMonitor(processes, time, NullLogger<OtherAppMonitor>.Instance)));
            var setup = host.App.Services.GetRequiredService<SetupService>();
            var state = State(setup);
            Assert.True(state["firstRunCompleted"]!.GetValue<bool>());
            Assert.True(state["outputHeld"]!.GetValue<bool>());
            Assert.Equal("HyperHDR", state["otherApp"]!["name"]!.GetValue<string>());

            Thread.Sleep(500);
            Assert.Equal(0, Written(output));
            Assert.Empty(host.Engine.Effects.Active);

            var changed = 0;
            setup.Changed += () => Interlocked.Increment(ref changed);
            processes.Stop("hyperhdr.exe");
            time.Advance(OtherAppMonitor.PollInterval);

            Assert.False(State(setup)["outputHeld"]!.GetValue<bool>());
            Assert.Null(State(setup)["otherApp"]);
            Assert.Equal(1, changed);
            Assert.Equal("Rainbow swirl fast", Assert.Single(host.Engine.Effects.Active).Name);
            Wait.Until(() => Written(output) > 0);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Autostart_StaysOff_UnlessTheWelcomeTurnsItOn()
    {
        var autostart = new FakeAutostart();
        await using var host = await TestHost.StartAsync(firstRun: true, configure: (_, services) => services.AddSingleton<IAutostart>(autostart));
        var setup = host.App.Services.GetRequiredService<SetupService>();

        Assert.False(autostart.IsEnabled);
        Assert.Equal("""{"available":true,"enabled":false}""", State(setup)["autostart"]!.ToJsonString());

        setup.Start(autostart: false);
        Assert.False(autostart.IsEnabled);
        setup.Start(autostart: true);
        Assert.True(autostart.IsEnabled);
        Assert.True(State(setup)["autostart"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Headless_HasNoAutostart()
    {
        await using var host = await TestHost.StartAsync();
        var setup = host.App.Services.GetRequiredService<SetupService>();
        Assert.False(State(setup)["autostart"]!["available"]!.GetValue<bool>());
        Assert.False(setup.SetAutostart(true));
    }

    private static JsonObject State(SetupService setup) => setup.State();

    private static async Task<WebApplication> RestartAsync(string dir)
    {
        var app = OverglowHost.Build(
            ["--data-dir", dir, "--legacy-data-dir", "none", "--bind", "127.0.0.1", "--port", "0", "--capture", "synthetic", "--ui-dir", Path.Combine(dir, "no-ui")],
            (_, services) => services.AddSingleton<IProcessList>(new FakeProcessList()));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
