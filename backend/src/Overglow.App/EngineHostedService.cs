using Microsoft.Extensions.Hosting;
using Overglow.Engine;

namespace Overglow.App;

/// <summary>
/// Starts the lighting with the host (after the <see cref="LightingGate"/> decided whether the output is held, so a
/// held start never opens a controller) and switches the controllers off (black frame, then closed) when it stops;
/// the <see cref="GracefulShutdown"/> waits for that.
/// </summary>
internal sealed class EngineHostedService(LightingEngine engine, LightingGate gate, GracefulShutdown shutdown) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        gate.Start();
        engine.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        gate.Dispose();
        engine.Dispose();
        shutdown.SignalLightsOff();
        return Task.CompletedTask;
    }
}
