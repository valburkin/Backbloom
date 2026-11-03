using System.Net;
using Backbloom.Devices.Setup;

namespace Backbloom.Tests.Fakes;

/// <summary>
/// This PC's Wi-Fi in the tests: a current network, what a scan finds, which network/password pairs join, and the
/// addresses the PC has (a 4.3.2.x one while it is on a board's hotspot). Nothing real is scanned or switched.
/// </summary>
internal sealed class FakeWifi : IWifi
{
    private readonly Lock _lock = new();

    public WifiState State { get; set; } = new(true, "Home", "Home", "00000000-0000-0000-0000-000000000001", WifiBlock.None);

    public List<WifiNetworkInfo> Networks { get; } = [];

    public WifiBlock ScanBlocked { get; set; }

    /// <summary>The network/password pairs that join (null password: an open network).</summary>
    public List<(string Ssid, string? Password)> Joinable { get; } = [];

    /// <summary>Every join attempt.</summary>
    public List<(string Ssid, string? Password)> Joins { get; } = [];

    /// <summary>Every way back, with the state it went back to.</summary>
    public List<WifiState> Restores { get; } = [];

    /// <summary>The network the PC is on now.</summary>
    public string? Current { get; private set; } = "Home";

    public List<IPAddress> Addresses { get; } = [IPAddress.Parse("192.168.1.20")];

    public int Scans { get; private set; }

    /// <summary>The passwords Windows keeps, by profile.</summary>
    public Dictionary<string, string> SavedKeys { get; } = new(StringComparer.Ordinal);

    public int KeyReads { get; private set; }

    /// <summary>The hotspot hands out an address (4.3.2.17) once joined.</summary>
    public bool GivesAddress { get; set; } = true;

    public IEnumerable<IPAddress> LocalAddresses()
    {
        lock (_lock)
        {
            return [.. Addresses];
        }
    }

    public WifiState GetState() => State with { Ssid = State.Blocked == WifiBlock.None ? State.Ssid : null };

    public string? ReadSavedKey(WifiState state)
    {
        KeyReads++;
        return state.Profile is { } profile && SavedKeys.TryGetValue(profile, out var key) ? key : null;
    }

    public Task<WifiScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        Scans++;
        return Task.FromResult(ScanBlocked == WifiBlock.None ? new WifiScanResult([.. Networks], WifiBlock.None) : new WifiScanResult([], ScanBlocked));
    }

    public Task<bool> JoinTemporaryAsync(string ssid, string? password, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Joins.Add((ssid, password));
            if (!Joinable.Contains((ssid, password)))
            {
                return Task.FromResult(false);
            }

            Current = ssid;
            Addresses.Clear();
            if (GivesAddress)
            {
                Addresses.Add(IPAddress.Parse("4.3.2.17"));
            }

            return Task.FromResult(true);
        }
    }

    public Task RestoreAsync(WifiState previous, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Restores.Add(previous);
            Current = previous.Ssid;
            Addresses.Clear();
            Addresses.Add(IPAddress.Parse("192.168.1.20"));
        }

        return Task.CompletedTask;
    }
}
