using Microsoft.Extensions.Hosting;
using Backbloom.Engine;

namespace Backbloom.App;

/// <summary>
/// Starts the lighting with the host (after the <see cref="LightingGate"/> decided whether the output is held, so a
/// held start never opens a controller) and switches the controllers off (black frame, then closed) when it stops;
/// the <see cref="GracefulShutdown"/> waits for that. The <see cref="ProfileMonitor"/> starts right after the engine.
/// </summary>
internal sealed class EngineHostedService(LightingEngine engine, LightingGate gate, ProfileMonitor profiles, GracefulShutdown shutdown) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        gate.Start();
        engine.Start();
        profiles.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        profiles.Dispose();
        gate.Dispose();
        engine.Dispose();
        shutdown.SignalLightsOff();
        return Task.CompletedTask;
    }
}
