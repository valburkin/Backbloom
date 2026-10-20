using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Overglow.Shell;

namespace Overglow.App;

/// <summary>The single-instance conversation between a new launch and the running app (see <see cref="SingleInstance"/>).</summary>
internal static class Launcher
{
    private static readonly TimeSpan s_answerTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A second start: the running app shows its window, or (headless) names the page to open here.</summary>
    public static async Task<int> ActivateRunningAsync(string dataDirectory)
    {
        var reply = await SingleInstance.SendAsync(dataDirectory, InstanceRequest.Activate, s_answerTimeout);
        if (reply is not { Accepted: true })
        {
            await Console.Error.WriteLineAsync($"Overglow is already running with {dataDirectory}, but doesn't answer.");
            return 1;
        }

        if (reply.Url is { } url)
        {
            new SystemBrowserLauncher().Open(url);
        }

        Console.WriteLine($"Overglow is already running with {dataDirectory}; showing it.");
        return 0;
    }

    /// <summary><c>--quit</c>: asks the running app to quit and waits until it is gone (LEDs off, files closed).</summary>
    public static async Task<int> QuitRunningAsync(string dataDirectory)
    {
        var reply = await SingleInstance.SendAsync(dataDirectory, InstanceRequest.Quit, TimeSpan.FromSeconds(3));
        if (reply is not { Accepted: true })
        {
            Console.WriteLine($"Overglow isn't running with {dataDirectory}.");
            return 0;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            using (var probe = SingleInstance.TryAcquire(dataDirectory))
            {
                if (probe is not null)
                {
                    Console.WriteLine("Overglow has quit.");
                    return 0;
                }
            }

            await Task.Delay(100);
        }

        await Console.Error.WriteLineAsync("Overglow didn't quit within 45 s.");
        return 1;
    }

    /// <summary>How the running app answers a later launch.</summary>
    public static InstanceReply Answer(WebApplication app, InstanceRequest request, Uri url)
    {
        switch (request)
        {
            case InstanceRequest.Quit:
                app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                return new InstanceReply(true);
            case InstanceRequest.Activate when app.Services.GetService<TrayMenu>() is { } menu:
                menu.OpenWindow();
                return new InstanceReply(true);
            case InstanceRequest.Activate:
                return new InstanceReply(true, url);
            default:
                return new InstanceReply(false);
        }
    }
}
