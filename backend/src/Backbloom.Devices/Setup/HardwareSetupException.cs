namespace Backbloom.Devices.Setup;

/// <summary>
/// A hardware setup step failed in a way the user can act on (wrong password, board busy, unknown strip type); the
/// message is meant for the UI as it is. Not an <see cref="IOException"/>, so handlers of I/O errors never mistake a
/// validation error for a lost connection.
/// </summary>
public sealed class HardwareSetupException : Exception
{
    public HardwareSetupException()
    {
    }

    public HardwareSetupException(string message)
        : base(message)
    {
    }

    public HardwareSetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
