using System.Numerics;
using System.Text;
using Backbloom.Devices.Drivers.Serial;
using Backbloom.Discovery.Usb;

namespace Backbloom.Devices.Tests;

/// <summary>Adalight / AWA, Skydimo and SP616E frames and the serial port handling, against a fake port.</summary>
public sealed class SerialDriverTests
{
    private static readonly byte[] s_rgb = [1, 2, 3, 4, 5, 6];

    private static (T Driver, FakeSerialFactory Ports) Create<T>(Func<DriverContext, T> create, params SerialPortInfo[] ports)
    {
        var factory = new FakeSerialFactory();
        var context = Ctx.Create(serial: factory, discovery: Ctx.Discovery(serial: new FakeSerialEnumerator(ports)));
        return (create(context), factory);
    }

    private static AdalightDriver Adalight(DriverContext context, string json) =>
        new(AdalightOptions.FromConfig(Ctx.Json(json)), context);

    [Fact]
    public async Task Adalight_ClassicFrame()
    {
        var (driver, ports) = Create(c => Adalight(c, """{ "output": "COM7", "rate": 115200 }"""));
        using (driver)
        {
            await driver.OpenAsync(2, TestContext.Current.CancellationToken);
            driver.Write(s_rgb);
        }

        var port = Assert.Single(ports.Created);
        Assert.Equal("COM7", port.PortName);
        Assert.Equal(new byte[] { (byte)'A', (byte)'d', (byte)'a', 0, 1, 0x54, 1, 2, 3, 4, 5, 6 }, Assert.Single(port.Writes));
    }

    [Fact]
    public async Task Adalight_AwaFrameEndsWithTheFletcherChecksum()
    {
        var (driver, ports) = Create(c => Adalight(c, """{ "output": "COM7", "awa_mode": true }"""));
        using (driver)
        {
            await driver.OpenAsync(2, TestContext.Current.CancellationToken);
            driver.Write(s_rgb);
        }

        Assert.Equal(new byte[] { (byte)'A', (byte)'w', (byte)'a', 0, 1, 0x54, 1, 2, 3, 4, 5, 6, 21, 56, 16 }, Assert.Single(ports.Created[0].Writes));
    }

    [Fact]
    public async Task Adalight_AwaWhiteCalibrationIsPartOfTheChecksum()
    {
        var (driver, ports) = Create(c => Adalight(c, """
            { "output": "COM7", "awa_mode": true, "white_channel_calibration": true, "white_channel_limit": 50,
              "white_channel_red": 10, "white_channel_green": 20, "white_channel_blue": 30 }
            """));
        using (driver)
        {
            await driver.OpenAsync(2, TestContext.Current.CancellationToken);
            driver.Write(s_rgb);
        }

        Assert.Equal(new byte[] { (byte)'A', (byte)'w', (byte)'A', 0, 1, 0x54, 1, 2, 3, 4, 5, 6, 128, 10, 20, 30, 209, 242, 214 }, Assert.Single(ports.Created[0].Writes));
    }

    [Fact]
    public void Fletcher_ReplacesTheHeaderLetterInTheExtendedSum()
    {
        var sum = new byte[3];
        AdalightProtocol.WriteFletcher([0x41], sum);
        Assert.Equal(new byte[] { 0x41, 0x41, 0xaa }, sum);
    }

    [Fact]
    public async Task Adalight_LightberryApa102Frame()
    {
        var (driver, ports) = Create(c => Adalight(c, """{ "output": "COM7", "lightberry_apa102_mode": true }"""));
        using (driver)
        {
            await driver.OpenAsync(2, TestContext.Current.CancellationToken);
            driver.Write(s_rgb);
        }

        Assert.Equal(
            new byte[] { (byte)'A', (byte)'d', (byte)'a', 0, 2, 0x57, 0, 0, 0, 0, 0xFF, 1, 2, 3, 0xFF, 4, 5, 6, 0, 0, 0, 0 },
            Assert.Single(ports.Created[0].Writes));
    }

    [Fact]
    public async Task Adalight_InfiniteRgbwFrame()
    {
        var (driver, ports) = Create(c => Adalight(c, """{ "output": "COM7", "awa_mode": true, "enable_ice_rgbw": true, "ice_white_led_intensity": 1.0 }"""));
        using (driver)
        {
            await driver.OpenAsync(1, TestContext.Current.CancellationToken);
            Assert.True(driver.WritesFloatColors);
            driver.WriteFloat([new Vector3(0, 0, 1)], TimeSpan.Zero);
        }

        // 'W' header, R G B W, Fletcher over the four bytes
        Assert.Equal(new byte[] { (byte)'A', (byte)'W', (byte)'a', 0, 0, 0x55, 0, 0, 255, 0, 0, 0, 2 }, ports.Created[0].Writes[0]);
    }

    [Fact]
    public async Task Skydimo_And_Sp616e_Frames()
    {
        var (skydimo, ports) = Create(c => new SkydimoDriver(SerialOptions.FromConfig(Ctx.Json("""{ "output": "COM3" }"""), 115200), c));
        using (skydimo)
        {
            await skydimo.OpenAsync(2, TestContext.Current.CancellationToken);
            skydimo.Write(s_rgb);
        }

        Assert.Equal(new byte[] { (byte)'A', (byte)'d', (byte)'a', 0, 0, 2, 1, 2, 3, 4, 5, 6 }, Assert.Single(ports.Created[0].Writes));

        var (sp, ports2) = Create(c => new Sp616eDriver(SerialOptions.FromConfig(Ctx.Json("""{ "output": "/dev/ttyACM0" }"""), 115200), c));
        using (sp)
        {
            await sp.OpenAsync(2, TestContext.Current.CancellationToken);
            sp.Write(s_rgb);
        }

        Assert.Equal("ttyACM0", ports2.Created[0].PortName);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 0xFF }, Assert.Single(ports2.Created[0].Writes));
    }

    [Fact]
    public void Auto_PrefersHyperSerialBoardsWithTheHandshake()
    {
        SerialPortInfo bt = new("COM1", @"\\.\COM1", "Standard Serial over Bluetooth link", null, null);
        SerialPortInfo ch340 = new("COM4", @"\\.\COM4", "USB-SERIAL CH340", 0x1a86, 0x7523);
        SerialPortInfo s2 = new("COM9", @"\\.\COM9", "ESP32-S2", 0x303a, 0x80c2);

        Assert.Equal("COM9", SerialLedDriver.PickAutomatically([bt, ch340, s2], espHandshake: true)!.PortName);
        Assert.Equal("COM4", SerialLedDriver.PickAutomatically([bt, ch340], espHandshake: true)!.PortName);
        Assert.Equal("COM4", SerialLedDriver.PickAutomatically([bt, ch340], espHandshake: false)!.PortName);
        Assert.Equal("COM1", SerialLedDriver.PickAutomatically([bt], espHandshake: false)!.PortName);
        Assert.Null(SerialLedDriver.PickAutomatically([], espHandshake: false));
    }

    [Fact]
    public async Task EspHandshake_WakesAPicoAndSaysGoodbyeOnClose()
    {
        var pico = new SerialPortInfo("COM5", @"\\.\COM5", "Pico", 0x2e8a, 0x000a);
        var (driver, ports) = Create(c => Adalight(c, """{ "output": "auto", "awa_mode": true, "espHandshake": true }"""), pico);
        ports.OnCreate = p => p.Feed(Encoding.ASCII.GetBytes("Welcome!\nAwa driver 10.0\n"));
        using (driver)
        {
            await driver.OpenAsync(2, TestContext.Current.CancellationToken);
            driver.Write(s_rgb);
            ports.Created[0].Feed(Encoding.ASCII.GetBytes("HyperSerialPico statistics: 1234 frames, 0 errors\n"));
            await driver.CloseAsync(TestContext.Current.CancellationToken);
        }

        var port = ports.Created[0];
        Assert.Equal("COM5", port.PortName);
        Assert.True(port.DtrEnable);
        Assert.True(port.RtsEnable);
        Assert.Equal(SerialLedDriver.EspHello, port.Writes[0]);
        Assert.Equal(15, port.Writes[1].Length);
        // two more black frames (the runner sends the first), then the goodbye frame
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0 }, port.Writes[2][6..12]);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0 }, port.Writes[3][6..12]);
        Assert.Equal(SerialLedDriver.EspGoodbye, port.Writes[4]);
        Assert.False(port.IsOpen);
    }

    [Fact]
    public async Task Write_ReportsALostPortToTheRunner()
    {
        var (driver, ports) = Create(c => Adalight(c, """{ "output": "COM7", "maxRetry": 3 }"""));
        using (driver)
        {
            await driver.OpenAsync(2, TestContext.Current.CancellationToken);
            ports.Created[0].FailWrite = _ => new TimeoutException();
            for (var i = 0; i < 5; i++)
            {
                Assert.False(driver.Write(s_rgb));
            }

            Assert.Throws<DeviceLostException>(() => driver.Write(s_rgb));

            ports.Created[0].FailWrite = _ => new IOException("The device was removed");
            Assert.Throws<DeviceLostException>(() => driver.Write(s_rgb));
            Assert.Equal(new RetryPolicy(3, TimeSpan.FromMilliseconds(2500)), driver.RetryPolicy);
        }
    }

    [Fact]
    public async Task Discover_ListsAutoAndEveryPort()
    {
        var context = Ctx.Create(discovery: Ctx.Discovery(serial: new FakeSerialEnumerator(new SerialPortInfo("COM3", @"\\.\COM3", "USB-SERIAL CH340", 0x1a86, 0x7523))));

        var devices = await SerialLedDriver.DiscoverAsync(Ctx.Request(context));

        Assert.Equal("""[{"value":"auto","name":"Auto"},{"value":"COM3","name":"USB-SERIAL CH340 (\\\\.\\COM3)"}]""", devices.ToJsonString());
    }

    [Fact]
    public void Printable_KeepsTheBannerCharacters() =>
        Assert.Equal("Awa driver 10 ok ", SerialLedDriver.Printable("Awa\u0001 driver 10\u00ff ok\n"u8.ToArray().Select(b => b).ToArray()));
}
