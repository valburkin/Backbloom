using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Backbloom.Devices.Setup;
using Backbloom.Discovery.Mdns;
using Backbloom.Discovery.Usb;
using Backbloom.Tests.Fakes;

namespace Backbloom.Devices.Tests;

/// <summary>
/// The one search and the Wi-Fi step against fakes only: fake USB boards, fake mDNS, fake WLED boards (one at a fake
/// 4.3.2.1, the hotspot, allowed by the injected address policy) and a fake Wi-Fi that never touches this PC's.
/// </summary>
public sealed class BoardFinderTests
{
    private static readonly BoardFinderTimes s_fast = new(
        TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5));

    private static readonly ImprovTimeouts s_improv = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    private static SerialPortInfo Esp(string port) => new(port, $@"\\.\{port}", "USB-SERIAL CH340", 0x1a86, 0x7523);

    private static MdnsServiceInstance Wled(string address, string host) => new(Discovery.KnownServices.Wled, host, host + ".local", IPAddress.Parse(address), 80, []);

    /// <summary>The fakes of one test.</summary>
    private sealed class Rig
    {
        public FakeImprovPorts Ports { get; } = new();

        public Dictionary<string, FakeWledBoard> Boards { get; } = new(StringComparer.Ordinal);

        public List<MdnsServiceInstance> Announced { get; } = [];

        public FakeWifi Wifi { get; } = new();

        public BoardFinder Create(params SerialPortInfo[] ports)
        {
            var mdns = new LiveMdns(this);
            var context = new DriverContext(NullLoggerFactory.Instance, new HttpClient(new RoutedHandler(this)), null, Resolve)
            {
                SerialPorts = Ports,
                Discovery = Ctx.Discovery(mdns: mdns, serial: new FakeSerialEnumerator(ports)),
            };
            var wled = new WledSetup(context, TimeSpan.Zero, _ => true, applyWait: TimeSpan.Zero);
            return new BoardFinder(context, new UsbBoards(context, s_improv), wled, Wifi, s_fast, Wifi.LocalAddresses);
        }

        private static ValueTask<IPAddress> Resolve(string host, CancellationToken cancellationToken) =>
            IPAddress.TryParse(host, out var a) ? ValueTask.FromResult(a) : ValueTask.FromException<IPAddress>(new System.Net.Sockets.SocketException(11001));
    }

    /// <summary>Answers each board's requests by the address they go to; nothing at other addresses.</summary>
    private sealed class RoutedHandler(Rig rig) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            FakeWledBoard? board;
            lock (rig.Boards)
            {
                rig.Boards.TryGetValue(request.RequestUri!.Host, out board);
            }

            if (board is null)
            {
                throw new HttpRequestException("No connection could be made");
            }

            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var (status, reply) = board.Handle(request.Method.Method, request.RequestUri.AbsolutePath, body);
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class LiveMdns(Rig rig) : IMdnsBrowser
    {
        public Task<IReadOnlyList<MdnsServiceInstance>> BrowseAsync(string serviceType, TimeSpan wait, CancellationToken cancellationToken)
        {
            lock (rig.Announced)
            {
                return Task.FromResult<IReadOnlyList<MdnsServiceInstance>>([.. rig.Announced.Where(a => a.ServiceType == serviceType)]);
            }
        }

        public Task<IPAddress?> ResolveAsync(string hostName, TimeSpan wait, CancellationToken cancellationToken) => Task.FromResult<IPAddress?>(null);
    }

    [Fact]
    public async Task Find_EveryRouteAtOnce_OneEntryPerBoard()
    {
        var rig = new Rig();
        rig.Ports.Add("COM3");
        var provisioned = rig.Ports.Add("COM4");
        provisioned.State = ImprovState.Provisioned;
        provisioned.Url = "http://192.168.1.60";

        // a board on the network that is also on USB (COM4), and one set up already
        var both = new FakeWledBoard();
        both.Info["mac"] = "aabbccddee01";
        var setUp = new FakeWledBoard();
        setUp.Info["name"] = "Desk";
        setUp.Info["mac"] = "aabbccddee02";
        rig.Boards["192.168.1.60"] = both;
        rig.Boards["192.168.1.61"] = setUp;
        rig.Announced.AddRange([Wled("192.168.1.60", "wled-1"), Wled("192.168.1.61", "wled-desk"), Wled("192.168.1.62", "wled-gone")]);
        rig.Wifi.Networks.AddRange([new("Home", 90, true), new("WLED-AP", 70, true), new("Neighbor", 40, true), new("wled-garage", 30, false)]);

        var found = await rig.Create(Esp("COM3"), Esp("COM4")).FindAsync([], CancellationToken.None);

        Assert.Equal(
            ["usb:COM3", "network:AA:BB:CC:DD:EE:01", "network:AA:BB:CC:DD:EE:02", "network:192.168.1.62", "hotspot:WLED-AP", "hotspot:wled-garage"],
            found.Boards.Select(b => b.Id));
        var usb = found.Boards[0];
        Assert.Equal((BoardRoutes.Usb, true, "COM3"), (usb.Via, usb.IsNew, usb.Port));
        var merged = found.Boards[1];
        Assert.Equal((BoardRoutes.Network, "COM4", "192.168.1.60", true, false, 30, "0.15.0"), (merged.Via, merged.Port, merged.Host, merged.IsNew, merged.SetUp, merged.LedCount, merged.Version));
        Assert.Equal(("Desk", false, true), (found.Boards[2].Name, found.Boards[2].IsNew, found.Boards[2].SetUp));
        Assert.Equal(("wled-gone", "wled-gone", null), (found.Boards[3].Name, found.Boards[3].MdnsName, found.Boards[3].Mac));

        // what tells the boards apart: the port and Improv's info, the hotspot's signal, the mDNS name
        Assert.Equal(("USB-SERIAL CH340 (COM3)", "WLED", "0.15.0", "esp32", "WLED"), (usb.PortName, usb.Firmware, usb.Version, usb.Chip, usb.DeviceName));
        Assert.Equal(("USB-SERIAL CH340 (COM4)", "wled-1", "AA:BB:CC:DD:EE:01"), (merged.PortName, merged.MdnsName, merged.Mac));
        Assert.Equal(70, found.Boards[4].Signal);
        Assert.False(found.SavedPassword);
        Assert.Equal((BoardRoutes.Hotspot, "WLED-AP", true), (found.Boards[4].Via, found.Boards[4].Hotspot, found.Boards[4].IsNew));
        Assert.Equal(("Home", true, WifiBlock.None), (found.Wifi.Ssid, found.Wifi.Adapter, found.Wifi.Blocked));
        Assert.Empty(found.UsbSilent);
    }

    [Fact]
    public async Task Find_SaysWhenLocationAccessHidesTheWifi()
    {
        var rig = new Rig();
        rig.Wifi.ScanBlocked = WifiBlock.Location;
        rig.Wifi.State = rig.Wifi.State with { Blocked = WifiBlock.Location };
        var found = await rig.Create().FindAsync([], CancellationToken.None);
        Assert.Empty(found.Boards);
        Assert.Equal((true, null, WifiBlock.Location), (found.Wifi.Adapter, found.Wifi.Ssid, found.Wifi.Blocked));
    }

    [Fact]
    public async Task Find_WithoutWifi_StillFindsTheOtherRoutes()
    {
        var rig = new Rig();
        rig.Wifi.State = WifiState.None;
        rig.Boards["192.168.1.60"] = new FakeWledBoard();
        rig.Announced.Add(Wled("192.168.1.60", "wled-1"));
        var found = await rig.Create().FindAsync([], CancellationToken.None);
        Assert.Single(found.Boards);
        Assert.False(found.Wifi.Adapter);
        Assert.Equal(0, rig.Wifi.Scans);
    }

    /// <summary>The newcomer's case: a factory-new board in WLED-AP mode, set up from the PC's own Wi-Fi.</summary>
    [Fact]
    public async Task Hotspot_SendsTheWifiComesBackAndFindsTheBoardByItsMac()
    {
        var rig = new Rig();
        var board = new FakeWledBoard();
        board.Info["mac"] = "a0b1c2d3e4f5";
        rig.Boards["4.3.2.1"] = board;
        rig.Wifi.Networks.Add(new("WLED-AP", 70, true));
        rig.Wifi.Joinable.Add(("WLED-AP", "wled1234"));
        var finder = rig.Create();
        var hotspot = (await finder.FindAsync([], CancellationToken.None)).Boards.Single();

        // once it has the Wi-Fi, the board restarts and shows up at home (another board answers there too)
        var other = new FakeWledBoard();
        other.Info["mac"] = "000000000001";
        rig.Boards["192.168.1.70"] = other;
        rig.Announced.Add(Wled("192.168.1.70", "wled-other"));
        var steps = new List<string>();
        var joining = finder.JoinWifiAsync(hotspot, "Home", "home-password", [], steps.Add, CancellationToken.None);
        await WaitForAsync(() => board.Restarts == 1);
        lock (rig.Boards)
        {
            rig.Boards["192.168.1.77"] = board;
        }

        lock (rig.Announced)
        {
            rig.Announced.Add(Wled("192.168.1.77", "wled-a0b1c2"));
        }

        var joined = await joining;

        Assert.Equal(new JoinedBoard("192.168.1.77", "http://192.168.1.77", "WLED", "A0:B1:C2:D3:E4:F5"), joined);
        Assert.Equal(["joining-hotspot", "sending", "rejoining", "waiting"], steps);
        Assert.Equal([("WLED-AP", "wled1234")], rig.Wifi.Joins);
        Assert.Equal("Home", Assert.Single(rig.Wifi.Restores).Ssid);
        Assert.Equal("Home", rig.Wifi.Current);
        Assert.Equal("Home", board.Cfg["nw"]!["ins"]![0]!["ssid"]!.GetValue<string>());
        Assert.Contains(board.Requests, r => r is { Method: "POST", Path: "/json/cfg" } && r.Body!.Contains("\"psk\":\"home-password\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hotspot_TriesAnOpenHotspotToo()
    {
        var rig = new Rig();
        rig.Boards["4.3.2.1"] = new FakeWledBoard();
        rig.Wifi.Joinable.Add(("WLED-AP", null));
        var hotspot = new FoundBoard("hotspot:WLED-AP", BoardRoutes.Hotspot, "WLED-AP", true) { Hotspot = "WLED-AP" };

        // the board never shows up at home: the password was wrong (or the network 5 GHz)
        var error = await Assert.ThrowsAsync<HardwareSetupException>(() => rig.Create().JoinWifiAsync(hotspot, "Home", "wrong", [], _ => { }, CancellationToken.None));
        Assert.StartsWith("The board didn’t show up on Home: check the Wi-Fi password", error.Message, StringComparison.Ordinal);
        Assert.Equal([("WLED-AP", "wled1234"), ("WLED-AP", null)], rig.Wifi.Joins);
        Assert.Single(rig.Wifi.Restores);
    }

    [Fact]
    public async Task Hotspot_JoinsTheWayTheScanSawIt_OneVisitAtATime_AndOnlyABoardsHotspot()
    {
        // seen with a password: only WLED's own; seen open: only open
        var rig = new Rig();
        rig.Boards["4.3.2.1"] = new FakeWledBoard();
        var secured = new FoundBoard("hotspot:WLED-AP", BoardRoutes.Hotspot, "WLED-AP", true) { Hotspot = "WLED-AP", HotspotSecure = true };
        await Assert.ThrowsAsync<HardwareSetupException>(() => rig.Create().JoinWifiAsync(secured, "Home", "x", [], _ => { }, CancellationToken.None));
        Assert.Equal([("WLED-AP", "wled1234")], rig.Wifi.Joins);

        rig = new Rig();
        var open = secured with { HotspotSecure = false };
        await Assert.ThrowsAsync<HardwareSetupException>(() => rig.Create().JoinWifiAsync(open, "Home", "x", [], _ => { }, CancellationToken.None));
        Assert.Equal([("WLED-AP", (string?)null)], rig.Wifi.Joins);

        // a second visit while one runs is refused (it would take the first's network for this PC's own)
        rig = new Rig();
        rig.Wifi.Joinable.Add(("WLED-AP", "wled1234"));
        rig.Wifi.GivesAddress = false;
        var finder = rig.Create();
        using var cancel = new CancellationTokenSource();
        var first = finder.JoinWifiAsync(secured, "Home", "x", [], s => { if (s == "joining-hotspot") { cancel.CancelAfter(300); } }, cancel.Token);
        await WaitForAsync(() => rig.Wifi.Joins.Count == 1);
        var second = await Assert.ThrowsAsync<HardwareSetupException>(() => finder.JoinWifiAsync(secured, "Home", "x", [], _ => { }, CancellationToken.None));
        Assert.StartsWith("This PC is joining a board’s Wi-Fi already", second.Message, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<Exception>(() => first);
        Assert.Single(rig.Wifi.Restores);

        // not a board's hotspot: never joined
        rig = new Rig();
        var home = new FoundBoard("hotspot:Neighbors", BoardRoutes.Hotspot, "Neighbors", true) { Hotspot = "Neighbors" };
        var error = await Assert.ThrowsAsync<HardwareSetupException>(() => rig.Create().JoinWifiAsync(home, "Home", "x", [], _ => { }, CancellationToken.None));
        Assert.Equal("Neighbors isn’t a WLED board’s Wi-Fi (their names start with WLED)", error.Message);
        Assert.Empty(rig.Wifi.Joins);
    }

    [Fact]
    public async Task Hotspot_AlwaysGoesBackToTheHomeNetwork()
    {
        // the hotspot can't be joined
        var rig = new Rig();
        var hotspot = new FoundBoard("hotspot:WLED-AP", BoardRoutes.Hotspot, "WLED-AP", true) { Hotspot = "WLED-AP" };
        var error = await Assert.ThrowsAsync<HardwareSetupException>(() => rig.Create().JoinWifiAsync(hotspot, "Home", "x", [], _ => { }, CancellationToken.None));
        Assert.StartsWith("This PC couldn’t join the board’s Wi-Fi (WLED-AP)", error.Message, StringComparison.Ordinal);
        Assert.Single(rig.Wifi.Restores);

        // joined, but no board answers at 4.3.2.1
        rig = new Rig();
        rig.Wifi.Joinable.Add(("WLED-AP", "wled1234"));
        await Assert.ThrowsAsync<HardwareSetupException>(() => rig.Create().JoinWifiAsync(hotspot, "Home", "x", [], _ => { }, CancellationToken.None));
        Assert.Equal("Home", Assert.Single(rig.Wifi.Restores).Ssid);
        Assert.Equal("Home", rig.Wifi.Current);

        // the board refuses the settings (locked with a PIN)
        rig = new Rig();
        rig.Wifi.Joinable.Add(("WLED-AP", "wled1234"));
        rig.Boards["4.3.2.1"] = new FakeWledBoard { Locked = true };
        var locked = await Assert.ThrowsAsync<HardwareSetupException>(() => rig.Create().JoinWifiAsync(hotspot, "Home", "x", [], _ => { }, CancellationToken.None));
        Assert.StartsWith("The board’s settings are locked", locked.Message, StringComparison.Ordinal);
        Assert.Single(rig.Wifi.Restores);

        // canceled while waiting for the hotspot's address
        rig = new Rig();
        rig.Wifi.Joinable.Add(("WLED-AP", "wled1234"));
        var finder = rig.Create();
        using var cancel = new CancellationTokenSource();
        var steps = new List<string>();
        rig.Wifi.GivesAddress = false;
        var task = finder.JoinWifiAsync(hotspot, "Home", "x", [], s => { steps.Add(s); if (s == "joining-hotspot") { cancel.CancelAfter(50); } }, cancel.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => task);
        Assert.Single(rig.Wifi.Restores);
    }

    [Fact]
    public async Task Hotspot_NeedsWifiOnThisPc()
    {
        var rig = new Rig();
        rig.Wifi.State = WifiState.None;
        var hotspot = new FoundBoard("hotspot:WLED-AP", BoardRoutes.Hotspot, "WLED-AP", true) { Hotspot = "WLED-AP" };
        var error = await Assert.ThrowsAsync<HardwareSetupException>(() => rig.Create().JoinWifiAsync(hotspot, "Home", "x", [], _ => { }, CancellationToken.None));
        Assert.Equal("This PC has no Wi-Fi: plug the board into this PC with a USB cable instead", error.Message);
        Assert.Empty(rig.Wifi.Joins);
        Assert.Empty(rig.Wifi.Restores);
    }

    [Fact]
    public async Task Usb_JoinsWithImprovAndReadsTheBoardAtItsAddress()
    {
        var rig = new Rig();
        var port = rig.Ports.Add("COM3");
        port.Url = "http://192.168.1.80";
        var board = new FakeWledBoard();
        board.Info["mac"] = "a0b1c2d3e4f6";
        rig.Boards["192.168.1.80"] = board;
        var finder = rig.Create(Esp("COM3"));
        var usb = (await finder.FindAsync([], CancellationToken.None)).Boards.Single();
        var steps = new List<string>();

        var joined = await finder.JoinWifiAsync(finder.Known(usb.Id)!, "Home", "right-password", [], steps.Add, CancellationToken.None);

        Assert.Equal(new JoinedBoard("192.168.1.80", "http://192.168.1.80", "WLED", "A0:B1:C2:D3:E4:F6"), joined);
        Assert.Equal(["sending"], steps);
        Assert.Empty(rig.Wifi.Joins);
    }

    [Fact]
    public async Task SavedPassword_IsReportedAndUsedButNeverReturned()
    {
        var rig = new Rig();
        var port = rig.Ports.Add("COM3");
        rig.Wifi.SavedKeys["Home"] = "right-password";
        var finder = rig.Create(Esp("COM3"));
        var found = await finder.FindAsync([], CancellationToken.None);
        Assert.True(found.SavedPassword);

        var joined = await finder.JoinWifiAsync(found.Boards.Single(), "Home", null, [], _ => { }, CancellationToken.None, useSavedPassword: true);
        Assert.Equal("Home", port.JoinedSsid);
        Assert.Equal("192.0.2.50", joined.Host);

        // only for the network this PC is on, and only when Windows keeps one
        var other = await Assert.ThrowsAsync<HardwareSetupException>(() => finder.JoinWifiAsync(found.Boards.Single(), "Office", null, [], _ => { }, CancellationToken.None, useSavedPassword: true));
        Assert.Equal("This PC has no saved password for Office: type it", other.Message);
        rig.Wifi.SavedKeys.Clear();
        Assert.False((await finder.FindAsync([], CancellationToken.None)).SavedPassword);
        await Assert.ThrowsAsync<HardwareSetupException>(() => finder.JoinWifiAsync(found.Boards.Single(), "Home", null, [], _ => { }, CancellationToken.None, useSavedPassword: true));
    }

    [Fact]
    public async Task SavedPassword_GoesToABoardThroughItsHotspot()
    {
        var rig = new Rig();
        var board = new FakeWledBoard();
        rig.Boards["4.3.2.1"] = board;
        rig.Wifi.Joinable.Add(("WLED-AP", "wled1234"));
        rig.Wifi.SavedKeys["Home"] = "saved-home-key";
        var hotspot = new FoundBoard("hotspot:WLED-AP", BoardRoutes.Hotspot, "WLED-AP", true) { Hotspot = "WLED-AP" };
        await Assert.ThrowsAsync<HardwareSetupException>(() => rig.Create().JoinWifiAsync(hotspot, "Home", null, [], _ => { }, CancellationToken.None, useSavedPassword: true));
        Assert.Contains(board.Requests, r => r is { Method: "POST", Path: "/json/cfg" } && r.Body!.Contains("\"psk\":\"saved-home-key\"", StringComparison.Ordinal));
    }
    [Fact]
    public void Hotspots_AreNamedLikeWled()
    {
        Assert.True(BoardFinder.IsBoardHotspot("WLED-AP"));
        Assert.True(BoardFinder.IsBoardHotspot("WLED-Desk"));
        Assert.False(BoardFinder.IsBoardHotspot("Home"));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }
}
