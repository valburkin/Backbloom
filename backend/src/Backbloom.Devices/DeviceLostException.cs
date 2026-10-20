namespace Backbloom.Devices;

/// <summary>
/// Thrown by <see cref="ILedDriver.Write"/> when the controller is gone (serial port unplugged, connection closed):
/// the runner closes the driver and reopens it with its retry policy.
/// </summary>
public sealed class DeviceLostException : IOException
{
    public DeviceLostException()
    {
    }

    public DeviceLostException(string message)
        : base(message)
    {
    }

    public DeviceLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
