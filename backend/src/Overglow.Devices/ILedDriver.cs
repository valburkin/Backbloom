namespace Overglow.Devices;

/// <summary>
/// The protocol part of an LED device: how to connect, send one frame and disconnect. Lifecycle, retries, refresh,
/// statistics and LED identification are handled once for every driver by <see cref="LedDeviceRunner"/>.
/// </summary>
/// <remarks>All members are called from the device's own thread, one at a time.</remarks>
public interface ILedDriver : IDisposable
{
    /// <summary>Driver name as used in the <c>device.type</c> setting ("wled", "ddp", ...).</summary>
    string Type { get; }

    /// <summary>Retry behavior when <see cref="OpenAsync"/> fails.</summary>
    RetryPolicy RetryPolicy { get; }

    /// <summary>Short live status for the UI, e.g. the Wi-Fi signal of a WLED controller.</summary>
    string? Status { get; }

    /// <summary>Connects and switches the controller on. Throws when the device can't be reached.</summary>
    ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken);

    /// <summary>
    /// Sends one frame: three bytes per LED, already in the controller's color order. Must not allocate.
    /// Returns false when the frame could not be sent.
    /// </summary>
    bool Write(ReadOnlySpan<byte> colors);

    /// <summary>Switches the controller off / releases it. The runner has already sent a black frame.</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken);
}
