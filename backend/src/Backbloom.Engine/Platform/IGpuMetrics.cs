namespace Backbloom.Engine.Platform;

/// <summary>
/// GPU load since the previous sample, as Task Manager shows it: the busiest engine (3D, copy, video, ...) of any
/// adapter, in percent; dedicated and shared GPU memory in use. Every value is null when unknown.
/// </summary>
/// <param name="ComputerPercent">All processes together.</param>
/// <param name="AppPercent">This process (the screen capture).</param>
/// <param name="ComputerDedicatedBytes">Dedicated GPU memory in use on all adapters.</param>
/// <param name="AppDedicatedBytes">This process's dedicated GPU memory.</param>
/// <param name="AppSharedBytes">This process's shared GPU memory (system memory the GPU uses).</param>
public readonly record struct GpuLoad(
    double? ComputerPercent, double? AppPercent, long? ComputerDedicatedBytes, long? AppDedicatedBytes, long? AppSharedBytes);

/// <summary>Samples the GPU load. Platform projects implement it; <see cref="NoGpuMetrics"/> elsewhere.</summary>
public interface IGpuMetrics
{
    /// <summary>The load since the previous call (the first call has no utilization yet).</summary>
    GpuLoad Sample();
}

/// <summary>For platforms without GPU counters: nothing is known.</summary>
public sealed class NoGpuMetrics : IGpuMetrics
{
    public GpuLoad Sample() => default;
}
