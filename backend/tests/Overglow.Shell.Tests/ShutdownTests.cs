using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Overglow.App;
using Overglow.Core.Color;
using Overglow.Platform.Windows.Desktop;

namespace Overglow.Shell.Tests;

/// <summary>Every way the app ends leaves the LEDs black (the file driver records each frame).</summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ShutdownTests
{
    private static string DeviceSettings(string output) =>
        new JsonObject
        {
            ["device"] = new JsonObject { ["type"] = "file", ["output"] = output, ["printTimeStamp"] = false },
        }.ToJsonString();

    private static string LastFrame(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return reader.ReadToEnd().TrimEnd().Split('\n')[^1].Trim();
    }

    private static async Task<(TestHost Host, string Output)> StartRedAsync()
    {
        var output = Path.Combine(Path.GetTempPath(), $"overglow-leds-{Guid.NewGuid():N}.txt");
        var host = await TestHost.StartAsync(DeviceSettings(output));
        host.Engine.SetColor(1, new Rgb8(255, 0, 0), 0, "test");
        Wait.Until(() => File.Exists(output) && LastFrame(output).StartsWith("[{255,", StringComparison.Ordinal));
        return (host, output);
    }

    [Fact]
    public async Task Stop_LeavesTheLedsBlack()
    {
        var (host, output) = await StartRedAsync();
        try
        {
            var shutdown = host.App.Services.GetRequiredService<GracefulShutdown>();
            Assert.False(shutdown.LightsOff);
            await host.App.StopAsync(TestContext.Current.CancellationToken);
            Assert.True(shutdown.LightsOff);
            Assert.Equal("[{0,0,0}]", LastFrame(output));
        }
        finally
        {
            await host.App.DisposeAsync();
            TestHost.DeleteDirectory(host.DataDirectory);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task EndOfTheWindowsSession_StopsAndWaitsForTheLeds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763), "Windows only");
        var (host, output) = await StartRedAsync();
        try
        {
            var running = host.App.WaitForShutdownAsync(TestContext.Current.CancellationToken);
            var events = host.App.Services.GetRequiredService<WindowsSystemEvents>();

            // As WM_ENDSESSION: the handler returns only once the LEDs are off (Windows ends the process then).
            await Task.Run(() => events.Raise(SystemEvent.EndSession), TestContext.Current.CancellationToken);
            Assert.True(host.App.Services.GetRequiredService<GracefulShutdown>().LightsOff);
            Assert.Equal("[{0,0,0}]", LastFrame(output));
            await running;
        }
        finally
        {
            await host.App.DisposeAsync();
            TestHost.DeleteDirectory(host.DataDirectory);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Quit_ByASecondLaunch_StopsTheHost()
    {
        await using var host = await TestHost.StartAsync();
        var lifetime = host.App.Services.GetRequiredService<IHostApplicationLifetime>();
        var reply = Launcher.Answer(host.App, InstanceRequest.Quit, new Uri("http://127.0.0.1:1/"));
        Assert.True(reply.Accepted);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task Activate_WithoutAWindow_NamesThePageToOpen()
    {
        await using var host = await TestHost.StartAsync();
        var reply = Launcher.Answer(host.App, InstanceRequest.Activate, new Uri("http://127.0.0.1:18890/"));
        Assert.Equal(new InstanceReply(true, new Uri("http://127.0.0.1:18890/")), reply);
    }
}
