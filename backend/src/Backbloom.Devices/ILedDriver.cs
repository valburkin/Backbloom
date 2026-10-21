using System.Numerics;

namespace Backbloom.Devices;

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

    /// <summary>
    /// The resend interval to use after the device opened, given the configured <c>refreshTime</c>: drivers whose
    /// protocol needs a fixed rate (Hue Entertainment streaming) or must not resend (REST lamps) override it.
    /// </summary>
    TimeSpan? EffectiveRefreshInterval(TimeSpan? configured) => configured;
}

/// <summary>
/// A driver whose controller reports about itself (power, brightness, temperature, ...). The driver reads it on its own
/// schedule, never in <see cref="ILedDriver.Write"/>, only while the device is open.
/// </summary>
public interface ITelemetryDriver : ILedDriver
{
    /// <summary>The latest report (null before the first); may be read from any thread.</summary>
    ControllerTelemetry? Telemetry { get; }
}

/// <summary>
/// A driver that can take the processed colors before they are rounded to 8 bits ("deep color": RGBW dithering by
/// the Infinite Color Engine, Hue Entertainment, LIFX). Port of <c>LedDevice::writeInfiniteColors</c>.
/// </summary>
public interface IFloatColorDriver : ILedDriver
{
    /// <summary>True when the current settings want <see cref="WriteFloat"/> instead of <see cref="ILedDriver.Write"/>.</summary>
    bool WritesFloatColors { get; }

    /// <summary>
    /// Sends one frame of 0..1 colors in RGB order: the driver applies the controller's color order itself. Must not
    /// allocate. <paramref name="frameInterval"/> is the device's refresh interval (zero when frames are sent as they
    /// come), used by the RGBW dithering.
    /// </summary>
    bool WriteFloat(ReadOnlySpan<Vector3> colors, TimeSpan frameInterval);
}
