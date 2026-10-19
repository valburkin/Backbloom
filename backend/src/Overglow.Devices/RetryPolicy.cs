namespace Overglow.Devices;

/// <summary>
/// How often and how long to wait before reopening a device that failed to open. Replaces the per-driver hard-coded
/// <c>setupRetry(ms)</c> calls of the C++ drivers; drivers provide their default delay, the user setting
/// <c>maxRetry</c> the attempt count.
/// </summary>
/// <param name="MaxRetries">Retries after the first failed attempt (0 = give up immediately).</param>
/// <param name="Delay">Wait before each retry.</param>
public sealed record RetryPolicy(int MaxRetries, TimeSpan Delay)
{
    /// <summary>The C++ default: 60 retries, 1.5 s apart (as for WLED).</summary>
    public static RetryPolicy Default { get; } = new(60, TimeSpan.FromMilliseconds(1500));

    public static RetryPolicy None { get; } = new(0, TimeSpan.Zero);

    /// <summary>Delay before the next attempt after <paramref name="failedAttempts"/> failures, or null to give up.</summary>
    public TimeSpan? NextDelay(int failedAttempts) => failedAttempts <= MaxRetries ? Delay : null;
}
