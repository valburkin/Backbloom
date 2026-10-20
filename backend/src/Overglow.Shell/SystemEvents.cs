namespace Overglow.Shell;

/// <summary>Power and session changes of the computer that the lighting reacts to.</summary>
public enum SystemEvent
{
    /// <summary>The computer goes to sleep or hibernates.</summary>
    Suspend,

    /// <summary>The computer woke up.</summary>
    Resume,

    /// <summary>The user's session was locked (not reported for remote desktop sessions).</summary>
    SessionLocked,

    /// <summary>The user's session was unlocked.</summary>
    SessionUnlocked,

    /// <summary>The session's display was switched off (power saving).</summary>
    DisplayOff,

    /// <summary>The session's display was switched on again.</summary>
    DisplayOn,

    /// <summary>The user logs off or the OS shuts down; the LEDs must be off before the handler returns.</summary>
    EndSession,
}

/// <summary>
/// Power and session notifications of the OS (port of HyperHDR's SuspendHandler). The Windows implementation listens
/// for <c>WM_POWERBROADCAST</c>, <c>WM_WTSSESSION_CHANGE</c> and <c>WM_ENDSESSION</c>.
/// </summary>
public interface ISystemEvents
{
    /// <summary>
    /// Raised on the platform's notification thread. Handlers must return quickly, except for
    /// <see cref="SystemEvent.EndSession"/>: the OS ends the process once the handlers returned, so they may block until
    /// the LEDs are off.
    /// </summary>
    event Action<SystemEvent>? Raised;
}

/// <summary><see cref="ISystemEvents"/> of platforms without notifications.</summary>
public sealed class NoSystemEvents : ISystemEvents
{
    public event Action<SystemEvent>? Raised
    {
        add { }
        remove { }
    }
}
