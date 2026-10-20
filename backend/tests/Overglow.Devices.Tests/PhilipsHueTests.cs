using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Overglow.Devices.Drivers.PhilipsHue;
using Overglow.Discovery;
using Overglow.Discovery.Mdns;
using Overglow.Discovery.Ssdp;

namespace Overglow.Devices.Tests;

/// <summary>Hue color conversion, stream messages, the REST flow against a fake bridge and DTLS streaming to a local server.</summary>
public sealed class PhilipsHueTests
{
    private const string ClientKey = "00112233445566778899AABBCCDDEEFF";

    [Fact]
    public void RgbToXyb_UsesTheWideGamutMatrix()
    {
        var red = HueProtocol.RgbToXyb(new Vector3(1, 0, 0), false);
        var sum = 0.664511f + 0.283881f + 0.000088f;
        Assert.Equal(new Vector3(0.664511f / sum, 0.283881f / sum, 1), red);
        Assert.Equal(Vector3.Zero, HueProtocol.RgbToXyb(new Vector3(0.000005f, 0, 0), false));
        Assert.Equal(0.5f, HueProtocol.RgbToXyb(new Vector3(0.5f, 0.5f, 0.5f), false).Z);
        Assert.True(HueProtocol.RgbToXyb(new Vector3(0.5f, 0.5f, 0.5f), true).Z < 0.25f);
    }

    [Fact]
    public void StreamV1_HeaderAndLights()
    {
        var packet = new byte[HueProtocol.V1Size(2)];
        var length = HueProtocol.WriteV1(packet, 7, [3, 5], [new Vector3(0.5f, 0.25f, 1), Vector3.Zero]);

        Assert.Equal(16 + 18, length);
        Assert.Equal(
            [.. "HueStream"u8.ToArray(), 0x01, 0x00, 7, 0, 0, 0x01, 0, 0, 0, 3, 0x7F, 0xFF, 0x3F, 0xFF, 0xFF, 0xFF, 0, 0, 5, 0, 0, 0, 0, 0, 0],
            packet);
    }

    [Fact]
    public void StreamV2_CarriesTheConfigurationId()
    {
        const string Id = "1a8d99cc-967b-44f2-9202-43f976c0fa6b";
        var packet = new byte[HueProtocol.V2Size(1, Id)];
        var length = HueProtocol.WriteV2(packet, 1, Id, [258], [new Vector3(1, 0, 0.5f)]);

        Assert.Equal(16 + 36 + 7, length);
        Assert.Equal([.. "HueStream"u8.ToArray(), 0x02, 0x00, 1, 0, 0, 0x01, 0], packet[..16]);
        Assert.Equal(Id, Encoding.ASCII.GetString(packet, 16, 36));
        Assert.Equal(new byte[] { 2, 0xFF, 0xFF, 0, 0, 0x7F, 0xFF }, packet[52..]);
    }

    [Fact]
    public void RestFormatting_FourSignificantDigitsLikeQt()
    {
        Assert.Equal("\"xy\":[0.7006,0.2993],\"bri\":254", HueProtocol.XyBri(new Vector3(0.700612f, 0.29930f, 1), 254));
        Assert.Equal("0.5", HueProtocol.Digits(0.5f));
        Assert.Equal(254, HueProtocol.RestBrightness(1, 1));
        Assert.Equal(1, HueProtocol.RestBrightness(0, 1));
        Assert.Equal(127, HueProtocol.RestBrightness(0.25f, 2));
    }

    private static string Bridge(string groups = "{}") => $$"""
        { "config": { "name": "Hue", "apiversion": "1.60.0" },
          "lights": { "1": { "name": "Left" }, "2": { "name": "Right" } },
          "groups": {{groups}} }
        """;

    [Fact]
    public async Task Rest_SwitchesOnSendsStatesAndSwitchesOff()
    {
        var http = new FakeHttpHandler((request, _) => request.Method == HttpMethod.Get ? FakeHttpHandler.Json(Bridge()) : FakeHttpHandler.Json("""[{"success":{}}]"""));
        var options = HueOptions.FromConfig(Ctx.Json("""
            { "output": "10.0.0.7", "username": "USER", "lightIds": ["2", "1", "9"], "switchOffOnBlack": false, "restoreOriginalState": false }
            """));
        using var driver = new PhilipsHueDriver(options, Ctx.Create(http: http));

        await driver.OpenAsync(3, TestContext.Current.CancellationToken);
        Assert.False(driver.WritesFloatColors);
        Assert.Equal(TimeSpan.FromMilliseconds(200), driver.EffectiveRefreshInterval(null));
        driver.Write([255, 0, 0, 0, 0, 0]);
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        var red = HueProtocol.XyBri(HueProtocol.RgbToXyb(new Vector3(1, 0, 0), false), 254);
        Assert.Equal(
        [
            (HttpMethod.Get, "http://10.0.0.7/api/USER/", null),
            (HttpMethod.Put, "http://10.0.0.7/api/USER/lights/1/state", """{"on": true }"""),
            (HttpMethod.Put, "http://10.0.0.7/api/USER/lights/2/state", """{"on": true }"""),
            (HttpMethod.Put, "http://10.0.0.7/api/USER/lights/1/state", "{\"on\":true,\"transitiontime\":1," + red + "}"),
            (HttpMethod.Put, "http://10.0.0.7/api/USER/lights/2/state", "{\"on\":true,\"transitiontime\":1,\"xy\":[0,0],\"bri\":1}"),
            (HttpMethod.Put, "http://10.0.0.7/api/USER/lights/1/state", """{"on": false }"""),
            (HttpMethod.Put, "http://10.0.0.7/api/USER/lights/2/state", """{"on": false }"""),
        ], http.Requests);
    }

    [Fact]
    public async Task Rest_BridgeErrorsFailTheOpen()
    {
        var http = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("""[{"error":{"type":1,"address":"/","description":"unauthorized user"}}]"""));
        using var driver = new PhilipsHueDriver(new HueOptions("10.0.0.7", "BAD"), Ctx.Create(http: http));

        var ex = await Assert.ThrowsAsync<HueApiException>(async () => await driver.OpenAsync(1, TestContext.Current.CancellationToken));
        Assert.Contains("unauthorized user", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entertainment_StreamsOverDtls()
    {
        using var bridge = new FakeDtlsBridge(Encoding.UTF8.GetBytes("USER"), Convert.FromHexString(ClientKey));
        var streamActive = false;
        var http = new FakeHttpHandler((request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/USER/")
            {
                return FakeHttpHandler.Json(Bridge("""{ "7": { "name": "TV", "type": "Entertainment", "lights": ["2", "1"] } }"""));
            }

            if (request.Method == HttpMethod.Get && path == "/api/USER/groups/7")
            {
                return FakeHttpHandler.Json($$"""{ "stream": { "active": {{(streamActive ? "true" : "false")}}, "owner": null } }""");
            }

            if (path == "/api/USER/groups/7")
            {
                streamActive = body!.Contains("true", StringComparison.Ordinal);
                return FakeHttpHandler.Json("[{\"success\":{\"/groups/7/stream/active\":" + (streamActive ? "true" : "false") + "}}]");
            }

            return FakeHttpHandler.Json("""[{"success":{}}]""");
        });
        var options = new HueOptions("10.0.0.7", "USER", ClientKey, Entertainment: true, GroupId: 7, RestoreOriginalState: false, SwitchOffOnBlack: false, StreamPort: bridge.Port);
        using var driver = new PhilipsHueDriver(options, Ctx.Create(http: http, resolveTo: "127.0.0.1"));

        await driver.OpenAsync(2, TestContext.Current.CancellationToken);
        Assert.True(driver.WritesFloatColors);
        Assert.Equal(TimeSpan.FromMilliseconds(20), driver.EffectiveRefreshInterval(TimeSpan.FromMilliseconds(100)));
        Assert.True(driver.WriteFloat([new Vector3(0.5f, 0.5f, 0.5f), new Vector3(0, 0, 1)], TimeSpan.FromMilliseconds(20)));
        var first = bridge.Next();
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        var white = HueProtocol.RgbToXyb(new Vector3(0.5f, 0.5f, 0.5f), false);
        var blue = HueProtocol.RgbToXyb(new Vector3(0, 0, 1), false);
        var expected = new byte[HueProtocol.V1Size(2)];
        HueProtocol.WriteV1(expected, 0, [1, 2], [white, blue]);
        Assert.Equal(expected, first);
        Assert.False(streamActive);
        Assert.Contains(http.Requests, r => r.Uri == "http://10.0.0.7/api/USER/lights/1/state" && r.Body == """{"on": true }""");
        Assert.Contains(http.Requests, r => r.Body == """{"stream":{"active":false}}""");
    }

    [Fact]
    public async Task Discovery_UsesMdnsThenSsdp()
    {
        var mdns = new FakeMdns(new MdnsServiceInstance(KnownServices.PhilipsHue, "Hue - 1A2B3C._hue._tcp.local.", "ecb5fa1a2b3c.local", IPAddress.Parse("10.0.0.7"), 443, []));
        var found = await PhilipsHueDriver.DiscoverAsync(Ctx.Request(Ctx.Create(discovery: Ctx.Discovery(mdns: mdns))));
        Assert.Equal("""[{"ip":"10.0.0.7","port":80,"value":"10.0.0.7","name":"Hue bridge 10.0.0.7 (ecb5fa1a2b3c.local)"}]""", found.ToJsonString());

        var ssdp = new FakeSsdp(
            new SsdpResponse(new IPEndPoint(IPAddress.Parse("10.0.0.8"), 1900), SsdpSearcher.ParseHeaders("HTTP/1.1 200 OK\r\nLOCATION: http://10.0.0.8:80/description.xml\r\nSERVER: Linux/3.14.0 UPnP/1.0 IpBridge/1.60.0\r\nST: upnp:rootdevice\r\nhue-bridgeid: 001788FFFE0A0B0C\r\n\r\n")),
            new SsdpResponse(new IPEndPoint(IPAddress.Parse("10.0.0.9"), 1900), SsdpSearcher.ParseHeaders("HTTP/1.1 200 OK\r\nLOCATION: http://10.0.0.9/x.xml\r\nSERVER: Router UPnP/1.0\r\n\r\n")));
        var viaSsdp = await PhilipsHueDriver.DiscoverAsync(Ctx.Request(Ctx.Create(discovery: Ctx.Discovery(ssdp: ssdp))));
        var bridge = Assert.Single(viaSsdp)!;
        Assert.Equal("10.0.0.8", bridge["ip"]!.GetValue<string>());
        Assert.Equal("001788FFFE0A0B0C", bridge["other"]!["hue-bridgeid"]!.GetValue<string>());
        Assert.Equal(("upnp:rootdevice", 1900), Assert.Single(ssdp.Searches));
    }

    [Fact]
    public async Task GetProperties_PairsWhenAsked()
    {
        var http = new FakeHttpHandler((request, _) => FakeHttpHandler.Json(request.Method == HttpMethod.Post
            ? """[{"success":{"username":"NEWUSER","clientkey":"ABCDEF"}}]"""
            : """{"name":"Hue"}"""));
        var context = Ctx.Create(http: http);

        var paired = await PhilipsHueDriver.GetPropertiesAsync(Ctx.Request(context, """{ "host": "10.0.0.7", "pair": true }"""));
        var config = await PhilipsHueDriver.GetPropertiesAsync(Ctx.Request(context, """{ "host": "10.0.0.7:8080", "user": "U", "filter": "config" }"""));

        Assert.Equal("""{"username":"NEWUSER","clientkey":"ABCDEF"}""", paired.ToJsonString());
        Assert.StartsWith("{\"devicetype\":\"overglow#", http.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal("http://10.0.0.7:8080/api/U/config", http.Requests[1].Uri);
        Assert.Equal("""{"name":"Hue"}""", config.ToJsonString());
    }

    /// <summary>A DTLS 1.2 PSK server (BouncyCastle) standing in for the bridge's port 2100.</summary>
    private sealed class FakeDtlsBridge : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly BlockingCollection<byte[]> _received = [];
        private readonly Thread _thread;

        public FakeDtlsBridge(byte[] identity, byte[] key)
        {
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _thread = new Thread(() => Serve(identity, key)) { IsBackground = true };
            _thread.Start();
        }

        public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

        public byte[] Next() => _received.TryTake(out var data, TimeSpan.FromSeconds(10)) ? data : throw new TimeoutException("No stream message received.");

        public void Dispose()
        {
            _socket.Dispose();
            _thread.Join(TimeSpan.FromSeconds(2));
            _received.Dispose();
        }

        private void Serve(byte[] identity, byte[] key)
        {
            try
            {
                var transport = new DtlsServerProtocol().Accept(new PskServer(new BcTlsCrypto(new SecureRandom()), new Identities(identity, key)), new ServerTransport(_socket));
                var buffer = new byte[1500];
                while (true)
                {
                    var n = transport.Receive(buffer, 0, buffer.Length, 10000);
                    if (n < 0)
                    {
                        return;
                    }

                    _received.Add(buffer[..n]);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or TlsFatalAlert or InvalidOperationException)
            {
            }
        }

        private sealed class Identities(byte[] identity, byte[] key) : TlsPskIdentityManager
        {
            public byte[] GetHint() => [];

            public byte[]? GetPsk(byte[] id) => id.AsSpan().SequenceEqual(identity) ? key : null;
        }

        private sealed class PskServer(BcTlsCrypto crypto, TlsPskIdentityManager identities) : PskTlsServer(crypto, identities)
        {
            protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

            protected override int[] GetSupportedCipherSuites() => [CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];
        }

        /// <summary>Answers whoever sent the last datagram.</summary>
        private sealed class ServerTransport(Socket socket) : DatagramTransport
        {
            private EndPoint _peer = new IPEndPoint(IPAddress.Loopback, 0);

            public int GetReceiveLimit() => 1472;

            public int GetSendLimit() => 1472;

            public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);

            public int Receive(Span<byte> buffer, int waitMillis)
            {
                if (!socket.Poll(waitMillis * 1000, SelectMode.SelectRead))
                {
                    return -1;
                }

                var temp = new byte[buffer.Length];
                var n = socket.ReceiveFrom(temp, ref _peer);
                temp.AsSpan(0, n).CopyTo(buffer);
                return n;
            }

            public void Send(byte[] buf, int off, int len) => socket.SendTo(buf, off, len, SocketFlags.None, _peer);

            public void Send(ReadOnlySpan<byte> buffer) => socket.SendTo(buffer.ToArray(), _peer);

            public void Close()
            {
            }
        }
    }
}
