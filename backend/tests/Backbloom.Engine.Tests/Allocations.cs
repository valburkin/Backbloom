using System.Runtime.ExceptionServices;

namespace Backbloom.Engine.Tests;

/// <summary>
/// Measures what code allocates with <see cref="GC.GetAllocatedBytesForCurrentThread"/>, made deterministic as in
/// Core.Tests: the warm-up and the measurement run on a new dedicated thread (no thread-pool work, no test framework
/// code on it), after a full collection. Measured on a test-runner thread under full-suite load, the counter
/// occasionally grew by a few KB without any allocation by the measured code.
/// </summary>
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