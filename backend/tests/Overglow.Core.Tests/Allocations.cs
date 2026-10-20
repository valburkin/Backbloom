using System.Runtime.ExceptionServices;

namespace Overglow.Core.Tests;

/// <summary>
/// Allocation tests run in this collection: alone, after the parallel tests, so nothing else in the process loads
/// types, JIT-compiles or initializes statics while they measure.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurements
{
    public const string Name = "Allocation measurements";
}

/// <summary>
/// Measures what code allocates with <see cref="GC.GetAllocatedBytesForCurrentThread"/>, made deterministic: the
/// warm-up and the measurement run on a new dedicated thread (no thread-pool work, no test framework code on it), and
/// a full collection before the measurement leaves the thread with an empty allocation context.
/// </summary>
/// <remarks>
/// Measured on a test-runner thread while other test classes ran in parallel, the counter occasionally grew by 4-6 KB
/// in the middle of an allocation-free loop under full-suite load (no JIT compilation and no GC on that thread in
/// that window; never reproduced without the parallel load), which made the hot-path tests fail spuriously.
/// </remarks>
internal static class Allocations
{
    /// <returns>The bytes <paramref name="measured"/> allocated.</returns>
    public static long Measure(Action warmUp, Action measured)
    {
        long allocated = -1;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                warmUp();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var before = GC.GetAllocatedBytesForCurrentThread();
                measured();
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Allocation measurement",
        };
        thread.Start();
        thread.Join();
        error?.Throw();
        return allocated;
    }
}
