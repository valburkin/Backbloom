using System.Text;
using Backbloom.Devices.Setup;
using Backbloom.Discovery.Usb;
using Backbloom.Tests.Fakes;

namespace Backbloom.Devices.Tests;

/// <summary>The Improv Wi-Fi serial codec, the client and the USB step of the guided setup, against fake boards.</summary>
public sealed class ImprovTests
{
    private static readonly ImprovTimeouts s_fast = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    private static SerialPortInfo Esp(string port) => new(port, $@"\\.\{port}", "Silicon Labs CP210x USB to UART Bridge", 0x10c4, 0xea60);

    private static (UsbBoards Usb, FakeImprovPorts Ports) Usb(params SerialPortInfo[] ports)
    {
        var fakes = new FakeImprovPorts();
        var context = Ctx.Create(serial: fakes, discovery: Ctx.Discovery(serial: new FakeSerialEnumerator(ports)));
        return (new UsbBoards(context, s_fast), fakes);
    }

    [Fact]
    public void Command_HasHeaderVersionTypeLengthChecksumAndNewline()
    {
        var packet = ImprovProtocol.Command(ImprovCommand.RequestDeviceInfo);
        Assert.Equal("IMPROV"u8.ToArray(), packet[..6]);
        Assert.Equal([1, 0x03, 2, 0x03, 0], packet[6..11]);

        // the sum of every byte before the checksum, low byte
        var sum = packet[..11].Sum(b => b) & 0xFF;
        Assert.Equal(sum, packet[11]);
        Assert.Equal((byte)'\n', packet[12]);
        Assert.Equal(13, packet.Length);
    }

    [Fact]
    public void WifiSettings_AreLengthPrefixed()
    {
        var data = ImprovProtocol.WifiSettings("Home", "pässword");
        var password = Encoding.UTF8.GetBytes("pässword");
        Assert.Equal([4, .. "Home"u8.ToArray(), (byte)password.Length, .. password], data);
        Assert.Throws<ArgumentException>(() => ImprovProtocol.WifiSettings(new string('s', 200), new string('p', 100)));
    }

    [Fact]
    public void TryRead_SkipsNoiseAndDamagedPacketsAndWaitsForTheRest()
    {
        var good = ImprovProtocol.Result(ImprovCommand.RequestDeviceInfo, "WLED", "0.15.0", "esp32", "Desk");
        var damaged = ImprovProtocol.Packet(ImprovPacketType.CurrentState, [2]);
        damaged[^2] ^= 0xFF;
        var buffer = new List<byte>();
        buffer.AddRange(Encoding.ASCII.GetBytes("boot: IMPRO\r\n"));
        buffer.AddRange(damaged);
        buffer.AddRange(good.AsSpan(0, 10).ToArray());
        Assert.Null(ImprovProtocol.TryRead(buffer));

        buffer.AddRange(good.AsSpan(10).ToArray());
        var packet = ImprovProtocol.TryRead(buffer);
        Assert.NotNull(packet);
        Assert.Equal(ImprovPacketType.RpcResult, packet.Type);
        var (command, strings) = packet.ReadResult();
        Assert.Equal(ImprovCommand.RequestDeviceInfo, command);
        Assert.Equal(["WLED", "0.15.0", "esp32", "Desk"], strings);
        Assert.Null(ImprovProtocol.TryRead(buffer));
    }

    [Fact]
    public void Probe_ReadsStateInfoAndAddress()
    {
        var board = new FakeImprovBoard("COM5") { State = ImprovState.Provisioned, Url = "http://192.0.2.77" };
        board.Open();
        var state = new ImprovClient(board).Probe(TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal(ImprovState.Provisioned, state.State);
        Assert.Equal(new ImprovDeviceInfo("WLED", "0.15.0", "esp32", "WLED"), state.Info);
        Assert.Equal("http://192.0.2.77", state.Address);

        var silent = new FakeImprovBoard("COM6") { Silent = true };
        silent.Open();
        Assert.Null(new ImprovClient(silent).Probe(TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public async Task Scan_OpensOnlyFreeEspPortsWithDtrAndRtsOff()
    {
        var other = new SerialPortInfo("COM1", @"\\.\COM1", "Communications Port", null, null);
        var (usb, ports) = Usb(other, Esp("COM3"), Esp("COM4"), Esp("COM7"));
        var ready = ports.Add("COM3");
        ports.Add("COM4");
        ports.Add("COM7").Silent = true;

        var scan = await usb.ScanAsync(["COM4"], CancellationToken.None);

        var found = Assert.Single(scan.Boards);
        Assert.Equal("COM3", found.Port);
        Assert.Equal("Silicon Labs CP210x USB to UART Bridge", found.Name);
        Assert.Equal(ImprovState.Ready, found.State);
        Assert.Equal("esp32", found.Info!.Chip);
        Assert.Null(found.Address);
        Assert.Equal(["COM7"], scan.Silent.Select(p => p.PortName));

        // COM1 isn't an ESP board and COM4 belongs to a device: neither is opened
        Assert.Equal([("COM3", 115200), ("COM7", 115200)], ports.Created);
        Assert.Equal((false, false), ready.SignalsAtOpen);
        Assert.False(ready.IsOpen);
        Assert.Equal(1, ready.Closes);
        Assert.Equal(0, ports.Boards["COM4"].Opens);

        // the silent board got a second chance (it may have been starting)
        Assert.Equal(1, ports.Boards["COM7"].Opens);
        Assert.Equal(4, ports.Boards["COM7"].Commands.Count);
    }

    [Fact]
    public async Task Scan_ListsABusyPortAsSilent()
    {
        var (usb, ports) = Usb(Esp("COM3"));
        ports.Add("COM3").Busy = true;
        var scan = await usb.ScanAsync([], CancellationToken.None);
        Assert.Empty(scan.Boards);
        Assert.Equal(["COM3"], scan.Silent.Select(p => p.PortName));
    }

    [Fact]
    public async Task Networks_StrongestFirstOncePerName()
    {
        var (usb, ports) = Usb(Esp("COM3"));
        ports.Add("COM3").Networks.AddRange([("Home", -70, true), ("Cafe", -50, false), ("Home", -40, true), ("", -30, false)]);

        var networks = await usb.NetworksAsync("COM3", [], CancellationToken.None);

        Assert.Equal(["Home", "Cafe"], networks.Select(n => n.Ssid));
        Assert.Equal(-40, networks[0].Rssi);
        Assert.Equal(100, networks[0].SignalPercent);
        Assert.Equal(100, networks[1].SignalPercent);
        Assert.True(networks[0].Secure);
        Assert.False(networks[1].Secure);
        Assert.Equal(40, new ImprovNetwork("x", -80, true).SignalPercent);
    }

    [Fact]
    public async Task Wifi_ReturnsTheAddressOrSaysThePasswordIsWrong()
    {
        var (usb, ports) = Usb(Esp("COM3"));
        var board = ports.Add("COM3");

        var error = await Assert.ThrowsAsync<HardwareSetupException>(() => usb.JoinWifiAsync("COM3", "Home", "wrong", [], CancellationToken.None));
        Assert.Equal("couldn’t join Home: check the password", error.Message);

        Assert.Equal("http://192.0.2.50", await usb.JoinWifiAsync("COM3", "Home", "right-password", [], CancellationToken.None));
        Assert.Equal("Home", board.JoinedSsid);
        Assert.Equal(ImprovState.Provisioned, board.State);
        Assert.False(board.IsOpen);
    }

    [Fact]
    public async Task Wifi_TimesOutWhenTheBoardDoesntAnswer()
    {
        var (usb, ports) = Usb(Esp("COM3"));
        ports.Add("COM3").Silent = true;
        var error = await Assert.ThrowsAsync<HardwareSetupException>(() => usb.JoinWifiAsync("COM3", "Home", "x", [], CancellationToken.None));
        Assert.Equal("The board didn’t join Home in time: check the network name and password", error.Message);
    }

    [Fact]
    public async Task PortsMustBeFreeEspBoards()
    {
        var (usb, ports) = Usb(Esp("COM3"), new SerialPortInfo("COM1", @"\\.\COM1", "Communications Port", null, null));
        ports.Add("COM3");

        Assert.Equal("COM3 is used by one of your devices", (await Assert.ThrowsAsync<HardwareSetupException>(() => usb.NetworksAsync("COM3", ["com3"], CancellationToken.None))).Message);
        Assert.Equal("There is no ESP board on COM1", (await Assert.ThrowsAsync<HardwareSetupException>(() => usb.NetworksAsync("COM1", [], CancellationToken.None))).Message);
        Assert.Equal("There is no ESP board on COM9", (await Assert.ThrowsAsync<HardwareSetupException>(() => usb.JoinWifiAsync("COM9", "Home", "x", [], CancellationToken.None))).Message);
        Assert.Equal("The network name must have 1 to 32 characters", (await Assert.ThrowsAsync<HardwareSetupException>(() => usb.JoinWifiAsync("COM3", new string('n', 33), "x", [], CancellationToken.None))).Message);
        Assert.Empty(ports.Created);
    }
}
