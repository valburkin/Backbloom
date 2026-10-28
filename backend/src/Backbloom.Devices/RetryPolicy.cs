namespace Backbloom.Devices;

/// <summary>
/// How often and how long to wait before reopening a device that failed to open. Replaces the per-driver hard-coded
/// <c>setupRetry(ms)</c> calls of the C++ drivers; drivers provide their default delay, the user setting
/// <c>maxRetry</c> the attempt count.
/// </summary>
/// <param name="MaxRetries">Quick retries after the first failed attempt.</param>
/// <param name="Delay">Wait before each quick retry.</param>
/// <param name="ThenEvery">
/// Wait before each further retry once the quick ones are used up, or null to give up then. Not in the C++ code, which
/// gave up and left the LEDs off until the user acted: a network controller that is back after a few minutes (a Wi-Fi
/// outage, its power switched off for a while) is used again.
/// </param>
public sealed record RetryPolicy(int MaxRetries, TimeSpan Delay, TimeSpan? ThenEvery = null)
{
    /// <summary>The slow pace of network controllers after their quick retries.</summary>
    public static readonly TimeSpan KeepTrying = TimeSpan.FromSeconds(30);

    /// <summary>The C++ default: 60 retries, 1.5 s apart (as for WLED), and then every <see cref="KeepTrying"/>.</summary>
    public static RetryPolicy Default { get; } = new(60, TimeSpan.FromMilliseconds(1500), KeepTrying);

    public static RetryPolicy None { get; } = new(0, TimeSpan.Zero);

    /// <summary>Delay before the next attempt after <paramref name="failedAttempts"/> failures, or null to give up.</summary>
    public TimeSpan? NextDelay(int failedAttempts) => failedAttempts <= MaxRetries ? Delay : ThenEvery;

    /// <summary>True when the attempt after <paramref name="failedAttempts"/> failures is the first slow one.</summary>
    public bool SlowsDownAfter(int failedAttempts) => failedAttempts == MaxRetries + 1 && ThenEvery is not null;
}
