namespace Overglow.Devices;

/// <summary>Lifecycle of one LED device, owned by its <see cref="LedDeviceRunner"/>.</summary>
public enum DeviceState
{
    /// <summary>Not connected (initial state, or switched off).</summary>
    Off,

    /// <summary>Connecting / handshaking / powering the controller on.</summary>
    Opening,

    /// <summary>Accepting frames.</summary>
    Ready,

    /// <summary>The last attempt to open failed; another one is scheduled by the retry policy.</summary>
    WaitingToRetry,

    /// <summary>Every attempt allowed by the retry policy failed; waits until it is switched on again.</summary>
    Failed,

    /// <summary>Writing a final black frame and powering the controller off.</summary>
    Closing,
}

/// <summary>
/// The allowed <see cref="DeviceState"/> transitions, enforced in one place instead of the five booleans of the C++
/// <c>LedDevice</c> (enabled, on, ready, in error, refresh).
/// </summary>
public sealed class DeviceStateMachine
{
    public DeviceState State { get; private set; } = DeviceState.Off;

    public static bool IsAllowed(DeviceState from, DeviceState to) => (from, to) switch
    {
        (DeviceState.Off, DeviceState.Opening) => true,
        (DeviceState.Opening, DeviceState.Ready or DeviceState.WaitingToRetry or DeviceState.Failed or DeviceState.Off) => true,
        (DeviceState.Ready, DeviceState.Closing or DeviceState.WaitingToRetry) => true,
        (DeviceState.WaitingToRetry, DeviceState.Opening or DeviceState.Off) => true,
        (DeviceState.Failed, DeviceState.Opening or DeviceState.Off) => true,
        (DeviceState.Closing, DeviceState.Off) => true,
        _ => false,
    };

    /// <summary>Moves to <paramref name="next"/>; throws when the transition is not allowed (a programming error).</summary>
    public void MoveTo(DeviceState next)
    {
        if (!IsAllowed(State, next))
        {
            throw new InvalidOperationException($"LED device state cannot change from {State} to {next}.");
        }

        State = next;
    }
}
