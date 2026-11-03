namespace Backbloom.Devices.Setup;

/// <summary>Why this PC's Wi-Fi can't be seen.</summary>
public enum WifiBlock
{
    /// <summary>Nothing blocks it.</summary>
    None,

    /// <summary>
    /// Windows 11 24H2 and newer hide network names from apps without location access (Settings → Privacy &amp; security
    /// → Location): the scan and the current network are refused.
    /// </summary>
    Location,
}

/// <summary>This PC's Wi-Fi as the guided setup sees it.</summary>
/// <param name="Adapter">The PC has a Wi-Fi adapter (and its WLAN service runs).</param>
/// <param name="Ssid">The network it is connected to; null when none (or hidden by <paramref name="Blocked"/>).</param>
/// <param name="Profile">The Windows profile of that connection, to rejoin it later; null when unknown.</param>
/// <param name="InterfaceId">Which adapter (the first that is connected, else the first).</param>
public sealed record WifiState(bool Adapter, string? Ssid, string? Profile, string? InterfaceId, WifiBlock Blocked)
{
    public static WifiState None { get; } = new(false, null, null, null, WifiBlock.None);
}

/// <summary>A network a Wi-Fi scan found.</summary>
public sealed record WifiNetworkInfo(string Ssid, int SignalPercent, bool Secure);

/// <summary>What a scan found, or why it couldn't look.</summary>
public sealed record WifiScanResult(IReadOnlyList<WifiNetworkInfo> Networks, WifiBlock Blocked);

/// <summary>
/// This PC's Wi-Fi for the guided setup: the current network, a scan for WLED access points, and a short visit to one
/// with a temporary profile, then back. Implementations never change or delete the user's own Wi-Fi profiles; they
/// delete only the temporary ones they created. Platform projects implement it (<see cref="NoWifi"/> elsewhere);
/// tests use fakes and never touch the real Wi-Fi.
/// </summary>
public interface IWifi
{
    /// <summary>The adapter and the current connection (read only).</summary>
    WifiState GetState();

    /// <summary>Scans for networks (a few seconds).</summary>
    Task<WifiScanResult> ScanAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The password Windows keeps for the current connection's profile (<paramref name="state"/>), or null when it has
    /// none (an open network, no profile known, or one this user can't read). Callers keep it only for the call that
    /// needs it and never log or return it.
    /// </summary>
    string? ReadSavedKey(WifiState state);

    /// <summary>
    /// Connects to <paramref name="ssid"/> with a temporary profile (WPA2-PSK with <paramref name="password"/>, open
    /// without one); true once connected, false when it didn't connect within <paramref name="timeout"/>.
    /// </summary>
    Task<bool> JoinTemporaryAsync(string ssid, string? password, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Goes back to <paramref name="previous"/> (its profile, else whatever Windows connects to by itself) and deletes
    /// the temporary profiles. Never throws: it is called on every way out of a visit.
    /// </summary>
    Task RestoreAsync(WifiState previous, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>A PC without Wi-Fi (other platforms, or no adapter).</summary>
public sealed class NoWifi : IWifi
{
    public WifiState GetState() => WifiState.None;

    public Task<WifiScanResult> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(new WifiScanResult([], WifiBlock.None));

    public string? ReadSavedKey(WifiState state) => null;

    public Task<bool> JoinTemporaryAsync(string ssid, string? password, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task RestoreAsync(WifiState previous, TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
}
