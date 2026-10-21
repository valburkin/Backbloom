using Microsoft.Extensions.Hosting;
using Backbloom.App.Logging;
using Backbloom.Engine.Diagnostics;

namespace Backbloom.App;

/// <summary>
/// Records the performance samples into the data directory's <c>logs/performance-yyyyMMdd.jsonl</c> while the app
/// runs. Registered after the <see cref="EngineHostedService"/>, so it starts after the engine and stops before it.
/// </summary>
internal sealed class PerformanceRecording(PerformanceRecorder recorder, PerformanceFile file) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        recorder.Recorded += file.Append;
        recorder.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        recorder.Dispose();
        recorder.Recorded -= file.Append;
        return Task.CompletedTask;
    }
}
