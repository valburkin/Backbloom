using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Backbloom.Discovery;
using Backbloom.Discovery.Usb;

namespace Backbloom.Devices.Setup;

/// <summary>How a board was found.</summary>
public static class BoardRoutes
{
    public const string Usb = "usb";
    public const string Network = "network";
    public const string Hotspot = "hotspot";
}

/// <summary>A WLED board the search found.</summary>
/// <param name="Id">"usb:COM3", "network:A0:B1:C2:D3:E4:F5" (or "network:&lt;address&gt;"), "hotspot:WLED-AP".</param>
/// <param name="Via">Where it was found: <see cref="BoardRoutes"/> (a board on USB and on the network is listed once, as network, with its port).</param>
/// <param name="IsNew">It still needs the setup: no Wi-Fi yet, its own hotspot, or WLED's defaults (<paramref name="SetUp"/> false).</param>
/// <param name="SetUp">For a network board: it looks set up (a name or LED count other than WLED's defaults, "WLED" and 30).</param>
public sealed record FoundBoard(string Id, string Via, string Name, bool IsNew)
{
    public string? Port { get; init; }

    public string? Host { get; init; }

    public string? Hotspot { get; init; }

    /// <summary>For a hotspot: whether the scan saw it with a password (null: not known).</summary>
    public bool? HotspotSecure { get; init; }

    public string? Mac { get; init; }

    public string? Version { get; init; }

    public int? LedCount { get; init; }

    public bool? SetUp { get; init; }

    /// <summary>USB: the port's name in Windows ("Silicon Labs CP210x USB to UART Bridge (COM5)").</summary>
    public string? PortName { get; init; }

    /// <summary>USB: Improv's device information.</summary>
    public string? Firmware { get; init; }

    public string? Chip { get; init; }

    public string? DeviceName { get; init; }

    /// <summary>Hotspot: its signal in percent.</summary>
    public int? Signal { get; init; }

    /// <summary>Network: the board's mDNS host name ("wled-a1b2c3"), which WLED makes from its MAC.</summary>
    public string? MdnsName { get; init; }
}

/// <summary>What one search found, and this PC's Wi-Fi.</summary>
/// <param name="SavedPassword">Windows keeps a password for the network this PC is on (<c>useSavedPassword</c> can use it).</param>
public sealed record FindResult(IReadOnlyList<FoundBoard> Boards, WifiState Wifi, IReadOnlyList<SerialPortInfo> UsbSilent, bool SavedPassword = false);

/// <summary>A board that joined the home network.</summary>
/// <param name="Host">The board on the home network (its address), for <c>wled-*</c>.</param>
/// <param name="Address">Its URL ("http://192.168.1.50").</param>
public sealed record JoinedBoard(string Host, string Address, string? Name, string? Mac);

/// <summary>The waits of the search and of a hotspot visit (shorter in tests).</summary>
/// <param name="Browse">The mDNS search for boards on the network.</param>
/// <param name="Info">Reading one board's <c>/json/info</c>.</param>
/// <param name="JoinHotspot">Joining a board's hotspot.</param>
/// <param name="HotspotAddress">Getting an address from the hotspot (4.3.2.x) once joined.</param>
/// <param name="Rejoin">Getting back to the previous network.</param>
/// <param name="Appear">The board showing up on the home network after it got the Wi-Fi.</param>
public sealed record BoardFinderTimes(TimeSpan Browse, TimeSpan Info, TimeSpan JoinHotspot, TimeSpan HotspotAddress, TimeSpan Rejoin, TimeSpan Appear)
{
    public static BoardFinderTimes Default { get; } = new(
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
}

/// <summary>
/// The guided setup's one search and its Wi-Fi step: finds WLED boards by every route at once (USB with Improv, the
/// home network with mDNS <c>_wled._tcp</c> and <c>/json/info</c>, and the boards' own hotspots with a Wi-Fi scan:
/// "WLED-AP" or names starting with "WLED"), and puts a board on the home network over USB (Improv) or through its
/// hotspot: the PC visits the hotspot with a temporary profile (WLED's password "wled1234", or open), sends the Wi-Fi
/// (<c>nw.ins</c>) to 4.3.2.1, always goes back to its own network, then finds the board there by its MAC.
/// </summary>
public sealed partial class BoardFinder
{
    /// <summary>WLED's access point password unless changed.</summary>
    public const string DefaultHotspotPassword = "wled1234";

    private const string DefaultName = "WLED";
    private const int DefaultLedCount = 30;

    private readonly DriverContext _context;
    private readonly UsbBoards _usb;
    private readonly WledSetup _wled;
    private readonly IWifi _wifi;
    private readonly BoardFinderTimes _times;
    private readonly Func<IEnumerable<IPAddress>> _localAddresses;
    private readonly ILogger _logger;
    private int _hotspotVisit;
    private volatile IReadOnlyList<FoundBoard> _last = [];

    /// <param name="localAddresses">This PC's interface addresses (to see the hotspot's 4.3.2.x); the live ones by default.</param>
    public BoardFinder(DriverContext context, UsbBoards usb, WledSetup wled, IWifi wifi, BoardFinderTimes? times = null, Func<IEnumerable<IPAddress>>? localAddresses = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(usb);
        ArgumentNullException.ThrowIfNull(wled);
        ArgumentNullException.ThrowIfNull(wifi);
        _context = context;
        _usb = usb;
        _wled = wled;
        _wifi = wifi;
        _times = times ?? BoardFinderTimes.Default;
        _localAddresses = localAddresses ?? (() => LocalNetwork.Addresses().Select(a => a.Address));
        _logger = context.LoggerFactory.CreateLogger<BoardFinder>();
    }

    /// <summary>The boards of the last search, by id (for <see cref="JoinWifiAsync"/>).</summary>
    public FoundBoard? Known(string id) => _last.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Searches every route at once (a few seconds); a route that fails just finds nothing.</summary>
    public async Task<FindResult> FindAsync(IReadOnlyCollection<string> portsInUse, CancellationToken cancellationToken)
    {
        var usb = Guard(_usb.ScanAsync(portsInUse, cancellationToken), new UsbScan([], []), "usb");
        var network = Guard(NetworkBoardsAsync(cancellationToken), [], "network");
        var wifi = Guard(HotspotsAsync(cancellationToken), (WifiState.None, []), "hotspot");
        await Task.WhenAll(usb, network, wifi);

        var (state, hotspots) = wifi.Result;
        var boards = Merge(usb.Result.Boards, network.Result, hotspots);
        _last = boards;
        LogFound(boards.Count, usb.Result.Boards.Count, network.Result.Count, hotspots.Count);

        // whether there is one, not the password itself
        var saved = state.Ssid is not null && _wifi.ReadSavedKey(state) is { Length: > 0 };
        return new FindResult(boards, state, usb.Result.Silent, saved);
    }

    /// <summary>
    /// Puts <paramref name="board"/> on <paramref name="ssid"/>: over USB with Improv, or through its hotspot (see the
    /// class). <paramref name="progress"/> hears the steps: "sending", "joining-hotspot", "rejoining", "waiting".
    /// </summary>
    /// <param name="password">The home network's password; null with <paramref name="useSavedPassword"/>.</param>
    /// <param name="useSavedPassword">
    /// Use the password Windows keeps for <paramref name="ssid"/> (the network this PC is on). It is read for this call
    /// only, sent to the board, and never logged, stored or returned.
    /// </param>
    public async Task<JoinedBoard> JoinWifiAsync(FoundBoard board, string ssid, string? password, IReadOnlyCollection<string> portsInUse, Action<string> progress, CancellationToken cancellationToken, bool useSavedPassword = false)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(ssid);
        ArgumentNullException.ThrowIfNull(progress);
        if (useSavedPassword)
        {
            var state = _wifi.GetState();
            password = string.Equals(state.Ssid, ssid, StringComparison.Ordinal) ? _wifi.ReadSavedKey(state) : null;
            if (string.IsNullOrEmpty(password))
            {
                throw new HardwareSetupException($"This PC has no saved password for {ssid}: type it");
            }
        }

        password ??= string.Empty;
        switch (board.Via)
        {
            case BoardRoutes.Usb when board.Port is { } port:
                progress("sending");
                var address = await _usb.JoinWifiAsync(port, ssid, password, portsInUse, cancellationToken) ??
                    throw new HardwareSetupException($"The board joined {ssid} but didn’t say its address: search again to find it");
                var host = HostOf(address);
                var info = await TryInfoAsync(host, cancellationToken);
                return new JoinedBoard(host, address, info is null ? board.Name : DeviceConfig.GetString(info, "name"), info is null ? null : WledSetup.FormatMac(DeviceConfig.GetString(info, "mac")));
            case BoardRoutes.Hotspot when board.Hotspot is { } hotspot:
                if (!IsBoardHotspot(hotspot))
                {
                    throw new HardwareSetupException($"{hotspot} isn’t a WLED board’s Wi-Fi (their names start with {DefaultName})");
                }

                // one visit at a time: a second would take the first's temporary network for this PC's own
                if (Interlocked.Exchange(ref _hotspotVisit, 1) == 1)
                {
                    throw new HardwareSetupException("This PC is joining a board’s Wi-Fi already: wait until it’s back on its own network");
                }

                try
                {
                    return await ThroughHotspotAsync(hotspot, board.HotspotSecure, ssid, password, progress, cancellationToken);
                }
                finally
                {
                    Volatile.Write(ref _hotspotVisit, 0);
                }
            case BoardRoutes.Network when board.Host is { } networkHost:
                // already on the network
                return new JoinedBoard(networkHost, $"http://{networkHost}", board.Name, board.Mac);
            default:
                throw new HardwareSetupException("Search again: that board isn’t there any more");
        }
    }

    // A board's hotspot: join it, send the Wi-Fi, always come back, then find the board at home by its MAC.
    private async Task<JoinedBoard> ThroughHotspotAsync(string hotspot, bool? secure, string ssid, string password, Action<string> progress, CancellationToken cancellationToken)
    {
        var previous = _wifi.GetState();
        if (!previous.Adapter)
        {
            throw new HardwareSetupException("This PC has no Wi-Fi: plug the board into this PC with a USB cable instead");
        }

        if (string.Equals(previous.Ssid, hotspot, StringComparison.Ordinal))
        {
            throw new HardwareSetupException($"This PC is connected to {hotspot}: connect it to your home network first");
        }

        string? mac;
        progress("joining-hotspot");
        try
        {
            LogJoiningHotspot(hotspot, previous.Ssid ?? "(none)");
            // WLED's own password when the scan saw one, an open network when it saw none (not known: both)
            if (!(secure != false && await _wifi.JoinTemporaryAsync(hotspot, DefaultHotspotPassword, _times.JoinHotspot, cancellationToken)) &&
                !(secure != true && await _wifi.JoinTemporaryAsync(hotspot, null, _times.JoinHotspot, cancellationToken)))
            {
                throw new HardwareSetupException($"This PC couldn’t join the board’s Wi-Fi ({hotspot}): move closer to the board, or plug it into this PC with a USB cable");
            }

            await WaitForHotspotAddressAsync(hotspot, cancellationToken);
            var info = await _wled.ReadInfoAsync(WledHosts.AccessPoint.ToString(), _times.Info + _times.Info, cancellationToken);
            mac = WledSetup.FormatMac(DeviceConfig.GetString(info, "mac"));
            progress("sending");
            await _wled.WriteAsync(WledHosts.AccessPoint.ToString(), new JsonObject { ["wifi"] = new JsonObject { ["ssid"] = ssid, ["password"] = password } }, cancellationToken);
        }
        finally
        {
            // always back to the PC's own network, also after a failure or a cancel
            progress("rejoining");
            await _wifi.RestoreAsync(previous, _times.Rejoin, CancellationToken.None);
            LogRejoined(previous.Ssid ?? "(none)");
        }

        progress("waiting");
        return await WaitForBoardAsync(mac, ssid, cancellationToken) ??
            throw new HardwareSetupException($"The board didn’t show up on {ssid}: check the Wi-Fi password, and that {ssid} is a 2.4 GHz network (WLED boards can’t join 5 GHz)");
    }

    private async Task WaitForHotspotAddressAsync(string hotspot, CancellationToken cancellationToken)
    {
        var deadline = _context.Time.GetUtcNow() + _times.HotspotAddress;
        while (!_localAddresses().Any(a => a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [4, 3, 2, _]))
        {
            if (_context.Time.GetUtcNow() > deadline)
            {
                throw new HardwareSetupException($"This PC joined the board’s Wi-Fi ({hotspot}) but got no address from it: try again");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), _context.Time, cancellationToken);
        }
    }

    // The board on the home network: the mDNS answer whose /json/info has its MAC (without a MAC it can't be told apart:
    // none is taken, and the search ends with "didn't show up").
    private async Task<JoinedBoard?> WaitForBoardAsync(string? mac, string ssid, CancellationToken cancellationToken)
    {
        var deadline = _context.Time.GetUtcNow() + _times.Appear;
        do
        {
            foreach (var board in await NetworkBoardsAsync(cancellationToken))
            {
                if (mac is not null && string.Equals(board.Mac, mac, StringComparison.OrdinalIgnoreCase) && board.Host is { } host)
                {
                    LogAppeared(mac, host, ssid);
                    return new JoinedBoard(host, $"http://{host}", board.Name, board.Mac);
                }
            }

            // the board restarts and joins (10-20 s): ask again in a moment
            await Task.Delay(TimeSpan.FromSeconds(1), _context.Time, cancellationToken);
        }
        while (_context.Time.GetUtcNow() < deadline);

        return null;
    }

    private async Task<IReadOnlyList<FoundBoard>> NetworkBoardsAsync(CancellationToken cancellationToken)
    {
        var services = await _context.Discovery.Mdns.BrowseAsync(KnownServices.Wled, _times.Browse, cancellationToken);
        var reads = services
            .DistinctBy(s => s.Address)
            .Select(async s =>
            {
                var ip = s.Address.ToString();
                var host = s.Port is 0 or 80 ? ip : $"{DeviceConfig.UrlHost(ip)}:{s.Port}";
                var info = await TryInfoAsync(host, cancellationToken);
                var mdnsName = MdnsName(s.HostName);
                if (info is null)
                {
                    return new FoundBoard($"{BoardRoutes.Network}:{host}", BoardRoutes.Network, mdnsName, false) { Host = host, MdnsName = mdnsName };
                }

                var name = DeviceConfig.GetString(info, "name");
                var leds = (info["leds"] as JsonObject) is { } l ? DeviceConfig.GetInt(l, "count", 0) : 0;
                var setUp = !string.Equals(name, DefaultName, StringComparison.Ordinal) || leds != DefaultLedCount;
                var mac = WledSetup.FormatMac(DeviceConfig.GetString(info, "mac"));
                return new FoundBoard($"{BoardRoutes.Network}:{mac ?? host}", BoardRoutes.Network, name.Length > 0 ? name : mdnsName, !setUp)
                {
                    Host = host,
                    MdnsName = mdnsName,
                    Mac = mac,
                    Version = DeviceConfig.GetString(info, "ver") is { Length: > 0 } v ? v : null,
                    LedCount = leds,
                    SetUp = setUp,
                };
            });
        return await Task.WhenAll(reads);
    }

    private async Task<(WifiState State, IReadOnlyList<FoundBoard> Hotspots)> HotspotsAsync(CancellationToken cancellationToken)
    {
        var state = _wifi.GetState();
        if (!state.Adapter)
        {
            return (state, []);
        }

        var scan = await _wifi.ScanAsync(cancellationToken);
        if (scan.Blocked != WifiBlock.None)
        {
            state = state with { Blocked = scan.Blocked };
        }

        var hotspots = scan.Networks
            .Where(n => IsBoardHotspot(n.Ssid) && !string.Equals(n.Ssid, state.Ssid, StringComparison.Ordinal))
            .Select(n => new FoundBoard($"{BoardRoutes.Hotspot}:{n.Ssid}", BoardRoutes.Hotspot, n.Ssid, true) { Hotspot = n.Ssid, Signal = n.SignalPercent, HotspotSecure = n.Secure })
            .ToList();
        return (state, hotspots);
    }

    /// <summary>A WLED board's own access point: "WLED-AP" (the default) or a name starting with "WLED".</summary>
    public static bool IsBoardHotspot(string ssid) => ssid.StartsWith(DefaultName, StringComparison.OrdinalIgnoreCase);

    // One entry per board: a USB board on Wi-Fi that the network search found too is that one, with its port
    // (Improv doesn't tell the MAC, so they are matched by address).
    internal static List<FoundBoard> Merge(IReadOnlyList<UsbBoard> usb, IReadOnlyList<FoundBoard> network, IReadOnlyList<FoundBoard> hotspots)
    {
        var boards = new List<FoundBoard>();
        var merged = network.ToList();
        foreach (var b in usb)
        {
            var host = b.Address is { } address ? HostOf(address) : null;
            var index = host is null ? -1 : merged.FindIndex(n => string.Equals(n.Host, host, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                merged[index] = merged[index] with { Port = b.Port, PortName = PortName(b), Firmware = b.Info?.Firmware, Chip = b.Info?.Chip, DeviceName = b.Info?.DeviceName };
                continue;
            }

            boards.Add(new FoundBoard($"{BoardRoutes.Usb}:{b.Port}", BoardRoutes.Usb, b.Info?.DeviceName is { Length: > 0 } d ? d : b.Name, b.State != ImprovState.Provisioned)
            {
                Port = b.Port,
                PortName = PortName(b),
                Host = host,
                Version = b.Info?.Version,
                Firmware = b.Info?.Firmware,
                Chip = b.Info?.Chip,
                DeviceName = b.Info?.DeviceName,
            });
        }

        boards.AddRange(merged);
        boards.AddRange(hotspots);
        return boards;
    }

    // "Silicon Labs CP210x USB to UART Bridge (COM5)", as Windows names the port
    private static string PortName(UsbBoard b) => string.Equals(b.Name, b.Port, StringComparison.OrdinalIgnoreCase) ? b.Port : $"{b.Name} ({b.Port})";

    // "wled-a1b2c3.local." -> "wled-a1b2c3"
    internal static string MdnsName(string hostName)
    {
        var name = hostName.TrimEnd('.');
        return name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;
    }

    private static string HostOf(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Authority : address;

    private async Task<JsonObject?> TryInfoAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            return await _wled.ReadInfoAsync(host, _times.Info, cancellationToken);
        }
        catch (HardwareSetupException)
        {
            return null;
        }
    }

    private async Task<T> Guard<T>(Task<T> route, T nothing, string name)
    {
        try
        {
            return await route;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            LogRouteFailed(name, ex.Message);
            return nothing;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Board search: {Boards} boards ({Usb} on USB, {Network} on the network, {Hotspots} hotspots)")]
    private partial void LogFound(int boards, int usb, int network, int hotspots);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Board search: the {Route} search failed: {Reason}")]
    private partial void LogRouteFailed(string route, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Board setup: joining the board's Wi-Fi {Hotspot} for a moment (this PC was on {Previous})")]
    private partial void LogJoiningHotspot(string hotspot, string previous);

    [LoggerMessage(Level = LogLevel.Information, Message = "Board setup: back on this PC's network {Previous}")]
    private partial void LogRejoined(string previous);

    [LoggerMessage(Level = LogLevel.Information, Message = "Board setup: the board {Mac} is on {Ssid} at {Host}")]
    private partial void LogAppeared(string mac, string host, string ssid);
}
