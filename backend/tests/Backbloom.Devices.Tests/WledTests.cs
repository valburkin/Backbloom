using System.Text.Json.Nodes;
using Backbloom.Devices.Drivers.Wled;

namespace Backbloom.Devices.Tests;

public sealed class WledTests
{
    private static byte[] Leds(int count) => [.. Enumerable.Range(0, count * 3).Select(i => (byte)i)];

    [Fact]
    public void Drgb_SmallStripIsOnePacketWithHeader()
    {
        var rgb = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var packet = new byte[WledProtocol.MaxPacketSize];

        var length = WledProtocol.WriteFrame(rgb, 0, packet);

        Assert.Equal(1, WledProtocol.PacketCount(3));
        Assert.Equal(new byte[] { 2, 255, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, packet[..length]);
    }

    [Fact]
    public void Drgb_490LedsStillFitOnePacket()
    {
        var rgb = Leds(490);
        var packet = new byte[WledProtocol.MaxPacketSize];

        var length = WledProtocol.WriteFrame(rgb, 0, packet);

        Assert.Equal(1, WledProtocol.PacketCount(490));
        Assert.Equal(2 + 1470, length);
        Assert.Equal(new byte[] { 2, 255 }, packet[..2]);
        Assert.Equal(rgb, packet[2..length]);
    }

    [Fact]
    public void Dnrgb_LargeStripIsSplitInto489LedChunksWithBigEndianStartIndex()
    {
        var rgb = Leds(1000);
        var packet = new byte[WledProtocol.MaxPacketSize];

        Assert.Equal(3, WledProtocol.PacketCount(1000));

        var l0 = WledProtocol.WriteFrame(rgb, 0, packet);
        Assert.Equal(4 + (489 * 3), l0);
        Assert.Equal(new byte[] { 4, 255, 0x00, 0x00 }, packet[..4]);
        Assert.Equal(rgb[..(489 * 3)], packet[4..l0]);

        var l1 = WledProtocol.WriteFrame(rgb, 1, packet);
        Assert.Equal(new byte[] { 4, 255, 0x01, 0xE9 }, packet[..4]);
        Assert.Equal(rgb[(489 * 3)..(978 * 3)], packet[4..l1]);

        var l2 = WledProtocol.WriteFrame(rgb, 2, packet);
        Assert.Equal(4 + (22 * 3), l2);
        Assert.Equal(new byte[] { 4, 255, 0x03, 0xD2 }, packet[..4]);
        Assert.Equal(rgb[(978 * 3)..], packet[4..l2]);
    }

    [Theory]
    [InlineData(true, true, 255, null, """{"on":true,"live":true,"bri":255}""")]
    [InlineData(true, true, 128, null, """{"on":true,"live":true,"bri":128}""")]
    [InlineData(true, false, 255, null, """{"on":true,"live":true}""")]
    [InlineData(false, true, 255, null, """{"on":false,"live":false}""")]
    [InlineData(false, true, 255, """{"on":true,"bri":40,"live":false}""", """{"on":true,"bri":40,"live":false}""")]
    public void OnOffRequest_MatchesCppStrings(bool on, bool overrideBrightness, int level, string? saved, string expected) =>
        Assert.Equal(expected, WledProtocol.OnOffRequest(on, overrideBrightness, level, saved));

    [Fact]
    public void Options_ParseHostPortAndDefaults()
    {
        var o = WledOptions.FromConfig(JsonNode.Parse("""{ "type": "wled", "host": "wled-test.local:8080", "maxRetry": 5 }""")!.AsObject());

        Assert.Equal(new WledOptions("wled-test.local", 8080, true, 255, false, 5), o);
    }

    [Fact]
    public async Task Driver_HandshakesStreamsAndSwitchesOff()
    {
        var http = new FakeHttpHandler((request, _) => request.Method == HttpMethod.Get
            ? FakeHttpHandler.Json("""
                { "state": { "on": true, "bri": 40 },
                  "info": { "ver": "0.15.0", "udpport": 21325, "leds": { "count": 3, "maxpwr": 0 }, "wifi": { "signal": 90, "channel": 6 } } }
                """)
            : FakeHttpHandler.Json("""{"success":true}"""));
        var udp = new RecordingChannel();
        using var driver = new WledDriver(new WledOptions("wled-test.local"), Contexts.With(http, udp));

        await driver.OpenAsync(3, TestContext.Current.CancellationToken);
        Assert.True(driver.Write(new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90 }));
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            (HttpMethod.Get, "http://wled-test.local/json", null),
            (HttpMethod.Put, "http://wled-test.local/json/state", """{"on":true,"live":true,"bri":255}"""),
            (HttpMethod.Put, "http://wled-test.local/json/state", """{"on":false,"live":false}"""),
        ], http.Requests);
        Assert.Equal("192.0.2.10:21325", udp.Remote!.ToString());
        Assert.Equal(new byte[] { 2, 255, 10, 20, 30, 40, 50, 60, 70, 80, 90 }, Assert.Single(udp.Packets));
        Assert.False(driver.Write(new byte[3]));
    }

    [Fact]
    public async Task Driver_RestoresTheSavedStateOnPowerOff()
    {
        var http = new FakeHttpHandler((request, _) => request.Method == HttpMethod.Get
            ? FakeHttpHandler.Json("""{ "state": { "on": true, "bri": 40, "live": true }, "info": { "leds": { "count": 1 } } }""")
            : FakeHttpHandler.Json("{}"));
        using var driver = new WledDriver(new WledOptions("10.0.0.5", RestoreOriginalState: true), Contexts.With(http, new RecordingChannel()));

        await driver.OpenAsync(1, TestContext.Current.CancellationToken);
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal("""{"on":true,"bri":40,"live":false}""", http.Requests[^1].Body);
    }

    [Fact]
    public async Task Driver_OpenFailsWhenTheControllerAnswersWithAnError()
    {
        var http = new FakeHttpHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        using var driver = new WledDriver(new WledOptions("wled-test.local"), Contexts.With(http, new RecordingChannel()));

        await Assert.ThrowsAsync<HttpRequestException>(async () => await driver.OpenAsync(3, TestContext.Current.CancellationToken));
        Assert.Equal(new RetryPolicy(60, TimeSpan.FromMilliseconds(1500)), driver.RetryPolicy);
    }

    [Fact]
    public void Driver_RequiresAHost() =>
        Assert.Throws<ArgumentException>(() => new WledDriver(new WledOptions(""), Contexts.With(new FakeHttpHandler((_, _) => new()), new RecordingChannel())));
}
