namespace Backbloom.Devices;

/// <summary>
/// What a controller reports about itself (<see cref="ITelemetryDriver"/>): its own power estimate and limit, brightness,
/// frame rate, signal, memory, uptime and temperature. Every value is null when the controller doesn't report it.
/// </summary>
/// <param name="ReadAt">When the controller was asked.</param>
public sealed record ControllerTelemetry(DateTimeOffset ReadAt)
{
    /// <summary>
    /// The controller's estimate of the current the LEDs draw now, its own share included (WLED: only while its power
    /// limiter is in use, globally or per output).
    /// </summary>
    public int? PowerMilliamps { get; init; }

    /// <summary>The controller's power limit (WLED's automatic brightness limiter, global or the outputs' sum); null when none is set.</summary>
    public int? PowerLimitMilliamps { get; init; }

    /// <summary>The current of one LED at full white the controller assumes (WLED's "LED current", averaged over its outputs).</summary>
    public double? MilliampsPerLed { get; init; }

    /// <summary>The controller's own brightness, 0..255, applied on top of the colors it receives (unless <see cref="ForceMaxBrightness"/>).</summary>
    public int? Brightness { get; init; }

    /// <summary>WLED's "force max brightness" for realtime data: <see cref="Brightness"/> is then ignored while streaming.</summary>
    public bool? ForceMaxBrightness { get; init; }

    public bool? On { get; init; }

    /// <summary>Frames per second the controller shows (WLED: updates of the LEDs, one per UDP packet above 489 LEDs).</summary>
    public int? FramesPerSecond { get; init; }

    /// <summary>The LED count the controller is set up for.</summary>
    public int? LedCount { get; init; }

    /// <summary>The controller shows realtime data (from Backbloom or another app: see <see cref="LiveSource"/>).</summary>
    public bool? Live { get; init; }

    /// <summary>Realtime protocol and sender, e.g. "UDP" and "192.168.1.20".</summary>
    public string? LiveMode { get; init; }

    public string? LiveSource { get; init; }

    /// <summary>WLED's realtime override (0 off, 1 until the realtime data ends, 2 until the controller restarts): realtime data is ignored while set.</summary>
    public int? RealtimeOverride { get; init; }

    /// <summary>Apps connected to the controller over WebSocket (WLED's own UI); -1 when it has no WebSocket support.</summary>
    public int? Clients { get; init; }

    /// <summary>Chip, e.g. "esp32" or "esp8266".</summary>
    public string? Chip { get; init; }

    public int? WifiChannel { get; init; }

    /// <summary>Wi-Fi signal quality, 0..100 %.</summary>
    public int? SignalPercent { get; init; }

    /// <summary>Wi-Fi signal strength in dBm.</summary>
    public int? Rssi { get; init; }

    public long? FreeMemoryBytes { get; init; }

    public long? UptimeSeconds { get; init; }

    /// <summary>
    /// Restarts noticed while this driver ran (its boot time moved; counted across reconnects, reset when the controller's
    /// settings change): a sign of power or heat problems.
    /// </summary>
    public int Restarts { get; init; }

    public double? TemperatureCelsius { get; init; }

    /// <summary>Which sensor <see cref="TemperatureCelsius"/> comes from (e.g. WLED's usermod name).</summary>
    public string? TemperatureSource { get; init; }

    /// <summary>The temperature is the controller chip's own (runs hotter than the LEDs or the power supply may).</summary>
    public bool TemperatureIsChip { get; init; }

    public string? Firmware { get; init; }
}
