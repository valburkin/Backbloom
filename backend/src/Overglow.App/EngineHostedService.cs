using Microsoft.Extensions.Hosting;
using Overglow.Engine;

namespace Overglow.App;

/// <summary>
/// Starts the lighting with the host and switches the controllers off (black frame, then closed) when it stops; the
/// <see cref="GracefulShutdown"/> waits for that.
/// </summary>
internal sealed class EngineHostedService(LightingEngine engine, GracefulShutdown shutdown) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        engine.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        engine.Dispose();
        shutdown.SignalLightsOff();
        return Task.CompletedTask;
    }
}
