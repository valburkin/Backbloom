using Microsoft.Extensions.Hosting;
using Overglow.Engine;

namespace Overglow.App;

/// <summary>Starts the lighting with the host and switches the controllers off when it stops.</summary>
internal sealed class EngineHostedService(LightingEngine engine) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        engine.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        engine.Dispose();
        return Task.CompletedTask;
    }
}
