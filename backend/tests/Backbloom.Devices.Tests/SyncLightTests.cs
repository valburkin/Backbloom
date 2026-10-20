using Backbloom.Devices.Drivers.Hid;
using Backbloom.Discovery.Usb;

namespace Backbloom.Devices.Tests;

/// <summary>Robobloq SyncLight frames (byte-exact with DriverHidSyncLight.cpp) and the open sequence against a fake HID device.</summary>
public sealed class SyncLightTests
{
    private static readonly HidDeviceInfo s_keyboard = new(@"\\?\hid#vid_1a86&pid_fe07&mi_00", "Robobloq", "SyncLight", "", 0x1a86, 0xfe07, 0x0001, 0x0006);
    private static readonly HidDeviceInfo s_lights = new(@"\\?\hid#vid_1a86&pid_fe07&mi_01", "Robobloq", "SyncLight", "SN1", 0x1a86, 0xfe07, 0xff00, 0x0001);

    [Fact]
    public void RbFrame_HasLengthIdActionPayloadAndAdditiveChecksum()
    {
        var frame = new byte[16];
        var length = SyncLightProtocol.WriteRb(frame, SyncLightProtocol.ActionBrightness, [0xFF], 1);

        Assert.Equal(new byte[] { 0x52, 0x42, 0x07, 0x01, 0x87, 0xFF, 0x22 }, frame[..length]);
    }

    [Fact]
    public void GlobalColor_AveragesTheLeds()
    {
        var frame = new byte[32];
        var length = SyncLightProtocol.WriteGlobalColor(frame, [10, 20, 30, 30, 40, 51], 65, 9);

        byte[] expected = [0x52, 0x42, 16, 9, 0x86, 1, 20, 30, 40, 65, 66, 0, 0, 0, 0xfe, 0];
        expected[^1] = SyncLightProtocol.Checksum(expected.AsSpan(0, 15));
        Assert.Equal(expected, frame[..length]);
    }

    [Fact]
    public void Segments_MergeTheCheapestAdjacentPairsFirst()
    {
        var protocol = new SyncLightProtocol();
        var frame = new byte[SyncLightProtocol.MaxFrameSize];

        var length = protocol.WriteSegments(frame, [255, 0, 0, 250, 0, 0, 0, 0, 255, 0, 0, 250], 4, 2, 3);

        byte[] expected = [(byte)'S', (byte)'C', 0, 17, 3, 0x80, 1, 253, 0, 0, 2, 3, 0, 0, 253, 4, 0];
        expected[^1] = SyncLightProtocol.Checksum(expected.AsSpan(0, 16));
        Assert.Equal(expected, frame[..length]);
    }

    [Fact]
    public void Segments_RepeatOrAverageLedsToTheControllerCount()
    {
        var protocol = new SyncLightProtocol();
        var frame = new byte[SyncLightProtocol.MaxFrameSize];

        // 2 LEDs on a 4-position controller: each LED covers two positions, nothing is merged
        var length = protocol.WriteSegments(frame, [10, 0, 0, 20, 0, 0], 4, 34, 1);

        Assert.Equal(7 + 20, length);
        Assert.Equal(new byte[] { 1, 10, 0, 0, 1, 2, 10, 0, 0, 2, 3, 20, 0, 0, 3, 4, 20, 0, 0, 4 }, frame[6..26]);
    }

    [Fact]
    public void Reports_Are65BytesWithReportIdZero()
    {
        var frame = Enumerable.Range(1, 100).Select(i => (byte)i).ToArray();
        var report = new byte[65];

        Assert.Equal(2, SyncLightProtocol.ReportCount(frame.Length));
        SyncLightProtocol.WriteReport(frame, 1, report);

        Assert.Equal(0, report[0]);
        Assert.Equal(frame[64..], report[1..37]);
        Assert.All(report[37..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void NextId_SkipsZero()
    {
        var protocol = new SyncLightProtocol();
        for (var i = 1; i <= 255; i++)
        {
            Assert.Equal(i, protocol.NextId());
        }

        Assert.Equal(1, protocol.NextId());
    }

    [Fact]
    public async Task Open_StopsEffectsSetsBrightnessAndReadsTheLedCount()
    {
        var device = new FakeHidDevice
        {
            Answer = report =>
            {
                if (report[5] != SyncLightProtocol.ActionReadInfo)
                {
                    return null;
                }

                var reply = new byte[64];
                (reply[0], reply[1], reply[2], reply[3], reply[8], reply[11], reply[21], reply[22], reply[23]) = ((byte)'R', (byte)'B', 24, report[4], 27, 65, 1, 2, 3);
                return reply;
            },
        };
        var opener = new FakeHidOpener(device);
        var context = Ctx.Create(hid: opener, discovery: Ctx.Discovery(hid: new FakeHidEnumerator(s_keyboard, s_lights)));
        using var driver = new SyncLightDriver(SyncLightOptions.FromConfig(Ctx.Json("""{ "brightness": 128, "outputMode": "global" }""")), context);

        await driver.OpenAsync(10, TestContext.Current.CancellationToken);
        Assert.True(driver.Write([100, 0, 0, 0, 0, 100]));

        Assert.Equal([s_lights.Path], opener.Opened);
        Assert.All(device.Reports, r => Assert.Equal(65, r.Length));
        Assert.Equal(
            [SyncLightProtocol.ActionStopEffect, SyncLightProtocol.ActionBrightness, SyncLightProtocol.ActionReadInfo, SyncLightProtocol.ActionBrightness, SyncLightProtocol.ActionColor],
            device.Reports.Select(r => r[5]));
        Assert.Equal(128, device.Reports[1][6]);
        Assert.Equal(new byte[] { 1, 50, 0, 50, 65, 66 }, device.Reports[4][6..12]);
        Assert.Equal("65 LEDs", driver.Status);
    }

    [Fact]
    public async Task Open_FailsWithoutALedCount()
    {
        var context = Ctx.Create(hid: new FakeHidOpener(new FakeHidDevice()), discovery: Ctx.Discovery(hid: new FakeHidEnumerator(s_lights)));
        using var driver = new SyncLightDriver(new SyncLightOptions(), context);

        await Assert.ThrowsAsync<IOException>(async () => await driver.OpenAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Discover_ListsOnlyTheLightingInterface()
    {
        var context = Ctx.Create(discovery: Ctx.Discovery(hid: new FakeHidEnumerator(s_keyboard, s_lights)));

        var devices = await SyncLightDriver.DiscoverAsync(Ctx.Request(context));

        Assert.Equal(2, devices.Count);
        Assert.Equal(s_lights.Path, devices[1]!["value"]!.GetValue<string>());
        Assert.Equal("SyncLight (0x1a86:0xfe07)", devices[1]!["name"]!.GetValue<string>());
        Assert.Equal("SN1", devices[1]!["serial"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("0x1a86", true, 0x1a86)]
    [InlineData("6790", true, 6790)]
    [InlineData("0755", true, 493)]
    [InlineData("0x10000", false, 0)]
    [InlineData("zz", false, 0)]
    public void Ids_ParseLikeQt(string text, bool ok, int value)
    {
        Assert.Equal(ok, SyncLightOptions.TryParseId(text, out var parsed));
        Assert.Equal(value, parsed);
    }
}
