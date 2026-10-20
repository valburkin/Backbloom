using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Discovery.Mdns;
using Overglow.Discovery.Ssdp;
using Overglow.Discovery.Usb;

namespace Overglow.Discovery.Tests;

/// <summary>mDNS messages, browsing and answering through a fake transport; SSDP and broadcast probes on loopback.</summary>
public sealed class DiscoveryTests
{
    private static readonly IPAddress s_wled = IPAddress.Parse("10.0.0.40");

    [Fact]
    public void Query_EncodesAPtrQuestion()
    {
        var bytes = DnsMessage.Query(new DnsQuestion("_wled._tcp.local.", DnsType.NamePointer)).Encode();

        Assert.Equal(
            [0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 5, .. "_wled"u8.ToArray(), 4, .. "_tcp"u8.ToArray(), 5, .. "local"u8.ToArray(), 0, 0, 12, 0, 1],
            bytes);
    }

    [Fact]
    public void Decode_FollowsNamePointers()
    {
        // answer: _wled._tcp.local PTR -> wled-desk.<ptr to _wled._tcp.local>
        byte[] message =
        [
            0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0,
            5, .. "_wled"u8.ToArray(), 4, .. "_tcp"u8.ToArray(), 5, .. "local"u8.ToArray(), 0,
            0, 12, 0, 1, 0, 0, 0x11, 0x94, 0, 12,
            9, .. "wled-desk"u8.ToArray(), 0xC0, 12,
        ];

        var decoded = DnsMessage.Decode(message);

        Assert.True(decoded.IsResponse);
        var answer = Assert.Single(decoded.Answers);
        Assert.Equal(("_wled._tcp.local.", DnsType.NamePointer, 4500u), (answer.Name, answer.Type, answer.Ttl));
        Assert.Equal("wled-desk._wled._tcp.local.", answer.Data);
        Assert.Throws<InvalidDataException>(() => DnsMessage.Decode(message.AsSpan(0, 20)));
    }

    [Fact]
    public void Records_RoundTrip()
    {
        var message = new DnsMessage
        {
            IsResponse = true,
            Answers = [new DnsRecord("i._wled._tcp.local.", DnsType.Srv, 120, new SrvData(0, 0, 80, "wled-desk.local."), CacheFlush: true)],
            Additionals = [new DnsRecord("wled-desk.local.", DnsType.A, 120, s_wled), new DnsRecord("i._wled._tcp.local.", DnsType.Txt, 4500, new List<string> { "mac=aabb" })],
        };

        var decoded = DnsMessage.Decode(message.Encode());

        Assert.Equal(new SrvData(0, 0, 80, "wled-desk.local."), decoded.Answers[0].Data);
        Assert.True(decoded.Answers[0].CacheFlush);
        Assert.Equal(s_wled, decoded.Additionals[0].Data);
        Assert.Equal(["mac=aabb"], (IEnumerable<string>)decoded.Additionals[1].Data);
    }

    private static byte[] WledAnnouncement(uint ttl) => new DnsMessage
    {
        IsResponse = true,
        Answers = [new DnsRecord(KnownServices.Wled, DnsType.NamePointer, ttl, "wled-desk._wled._tcp.local.")],
        Additionals =
        [
            new DnsRecord("wled-desk._wled._tcp.local.", DnsType.Srv, ttl, new SrvData(0, 0, 80, "wled-desk.local.")),
            new DnsRecord("wled-desk.local.", DnsType.A, ttl, s_wled),
        ],
    }.Encode();

    [Fact]
    public async Task Browse_QueriesAndReportsResolvedInstances()
    {
        var transport = new FakeTransport();
        using var mdns = new MdnsService(transport, NullLogger<MdnsService>.Instance, isOwnAddress: a => a.Equals(IPAddress.Parse("10.0.0.2")));
        transport.OnSend = _ => transport.Deliver(WledAnnouncement(120), new IPEndPoint(s_wled, 5353));

        var found = await mdns.BrowseAsync("_wled._tcp.local", TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        var query = DnsMessage.Decode(transport.Multicast.First());
        Assert.Equal(new DnsQuestion("_wled._tcp.local.", DnsType.NamePointer), Assert.Single(query.Questions));
        Assert.Equal(new MdnsServiceInstance(KnownServices.Wled, "wled-desk._wled._tcp.local.", "wled-desk.local", s_wled, 80, []), Assert.Single(found));

        transport.OnSend = null;
        transport.Deliver(WledAnnouncement(0), new IPEndPoint(s_wled, 5353));
        Assert.Empty(await mdns.BrowseAsync(KnownServices.Wled, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Browse_SkipsOwnAddresses()
    {
        var transport = new FakeTransport();
        using var mdns = new MdnsService(transport, NullLogger<MdnsService>.Instance, isOwnAddress: a => a.Equals(s_wled));
        transport.OnSend = _ => transport.Deliver(WledAnnouncement(120), new IPEndPoint(s_wled, 5353));

        Assert.Empty(await mdns.BrowseAsync(KnownServices.Wled, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));
    }

    private static readonly MdnsAdvertisement s_webUi = new(KnownServices.WebUi, "MY-PC:18290", "MY-PC.local.", 18290, ["version=0.1.0"], [IPAddress.Parse("10.0.0.2")]);

    [Fact]
    public void Responder_AnswersPtrSrvAndAddressQuestions()
    {
        var ptr = MdnsService.Respond(s_webUi, new DnsQuestion("_overglow-http._tcp.local.", DnsType.NamePointer))!;
        Assert.Equal("MY-PC:18290._overglow-http._tcp.local.", ptr.Answers[0].Data);
        Assert.Equal([DnsType.Srv, DnsType.Txt, DnsType.A], ptr.Additionals.Select(r => r.Type));
        Assert.Equal(new SrvData(0, 0, 18290, "MY-PC.local."), ptr.Additionals[0].Data);

        Assert.Equal(KnownServices.WebUi, MdnsService.Respond(s_webUi, new DnsQuestion(MdnsService.ServicesMetaQuery, DnsType.NamePointer))!.Answers[0].Data);
        Assert.Equal(DnsType.Srv, MdnsService.Respond(s_webUi, new DnsQuestion("my-pc:18290._overglow-http._tcp.local", DnsType.Any))!.Answers[0].Type);
        Assert.Equal(IPAddress.Parse("10.0.0.2"), MdnsService.Respond(s_webUi, new DnsQuestion("MY-PC.local.", DnsType.A))!.Answers[0].Data);
        Assert.Null(MdnsService.Respond(s_webUi, new DnsQuestion("_wled._tcp.local.", DnsType.NamePointer)));
    }

    [Fact]
    public void Responder_AnnouncesAndAnswersLegacyQueriesDirectly()
    {
        var transport = new FakeTransport();
        using (var mdns = new MdnsService(transport, NullLogger<MdnsService>.Instance))
        {
            mdns.Advertise(s_webUi);
            var announcement = DnsMessage.Decode(transport.Multicast.First());
            Assert.Equal(4500u, announcement.Answers[0].Ttl);

            var query = new DnsMessage { Id = 77, Questions = [new DnsQuestion(KnownServices.WebUi, DnsType.NamePointer)] }.Encode();
            transport.Deliver(query, new IPEndPoint(IPAddress.Parse("10.0.0.50"), 50123));
            var (reply, to) = Assert.Single(transport.Unicast);
            Assert.Equal("10.0.0.50:50123", to.ToString());
            Assert.Equal(77, DnsMessage.Decode(reply).Id);
        }

        var goodbye = DnsMessage.Decode(transport.Multicast.Last());
        Assert.All(goodbye.Answers, r => Assert.Equal(0u, r.Ttl));
    }

    [Fact]
    public void Ssdp_RequestAndHeaderParsingMatchTheCppCode()
    {
        Assert.Equal(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1982\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: wifi_bulb\r\n\r\n",
            SsdpSearcher.SearchRequest(new IPEndPoint(SsdpSearcher.MulticastAddress, 1982), "wifi_bulb"));

        var headers = SsdpSearcher.ParseHeaders("HTTP/1.1 200 OK\r\nLocation: yeelight://10.0.0.60:55443\r\nSERVER:  POSIX   UPnP/1.0 \r\nbroken line\r\n");
        Assert.Equal("yeelight://10.0.0.60:55443", headers["LOCATION"]);
        Assert.Equal("POSIX UPnP/1.0", headers["SERVER"]);
        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public async Task Ssdp_CollectsAnswersFromAResponder()
    {
        using var responder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        responder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)responder.LocalEndPoint!).Port;
        var request = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            var buffer = new byte[2048];
            var r = await responder.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0));
            request.SetResult(Encoding.UTF8.GetString(buffer, 0, r.ReceivedBytes));
            await responder.SendToAsync(Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: http://127.0.0.1/description.xml\r\nST: upnp:rootdevice\r\n\r\n"), SocketFlags.None, r.RemoteEndPoint);
        }, TestContext.Current.CancellationToken);

        var searcher = new SsdpSearcher(p => new IPEndPoint(IPAddress.Loopback, p));
        var answers = await searcher.SearchAsync("upnp:rootdevice", port, TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\nHOST: 127.0.0.1:", await request.Task, StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1/description.xml", Assert.Single(answers).Location);
    }

    [Fact]
    public async Task Probe_CollectsReplies()
    {
        using var lamp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        lamp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)lamp.LocalEndPoint!).Port;
        _ = Task.Run(async () =>
        {
            var buffer = new byte[256];
            var r = await lamp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0));
            await lamp.SendToAsync(buffer.AsMemory(0, r.ReceivedBytes), SocketFlags.None, r.RemoteEndPoint);
        }, TestContext.Current.CancellationToken);

        var probe = new UdpProbe(p => [new IPEndPoint(IPAddress.Loopback, p)]);
        var replies = await probe.ProbeAsync("hello"u8.ToArray(), port, TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

        Assert.Equal("hello", Encoding.UTF8.GetString(Assert.Single(replies).Data));
    }

    [Theory]
    [InlineData(@"\\?\USB#VID_1A86&PID_7523#5&2b0e3a2&0&3#{86e0d1e0-8089-11d0-9ce4-08003e301f73}", 0x1a86, 0x7523)]
    [InlineData(@"\\?\FTDIBUS#VID_0403+PID_6001+A50285BIA#0000#{86e0d1e0-8089-11d0-9ce4-08003e301f73}", 0x0403, 0x6001)]
    [InlineData("/dev/ttyUSB0", null, null)]
    public void Serial_UsbIdsFromTheDevicePath(string path, int? vendor, int? product)
    {
        var (v, p) = SerialPortEnumerator.UsbIds(path);
        Assert.Equal(vendor, v);
        Assert.Equal(product, p);
    }

    [Fact]
    public void Serial_PortNames()
    {
        Assert.Equal("COM3", SerialPortEnumerator.PortNameOf(@"\\.\COM3"));
        Assert.Equal("ttyUSB0", SerialPortEnumerator.PortNameOf("/dev/ttyUSB0"));
        Assert.Equal("USB-SERIAL CH340", SerialPortEnumerator.Description("USB-SERIAL CH340 (COM3)", "COM3"));
        Assert.True(new SerialPortInfo("COM9", "", "", 0x303a, 0x80c2).IsKnownEspOrPico);
        Assert.True(new SerialPortInfo("COM4", "", "", 0x10c4, 0xea60).IsLikelyEsp);
    }

    private sealed class FakeTransport : IMdnsTransport
    {
        private Action<byte[], IPEndPoint>? _received;

        public bool CanAdvertise => true;

        public ConcurrentQueue<byte[]> Multicast { get; } = new();

        public ConcurrentQueue<(byte[] Message, IPEndPoint To)> Unicast { get; } = new();

        public Action<byte[]>? OnSend { get; set; }

        public void Start(Action<byte[], IPEndPoint> received) => _received = received;

        public void SendMulticast(byte[] message)
        {
            Multicast.Enqueue(message);
            OnSend?.Invoke(message);
        }

        public void SendUnicast(byte[] message, IPEndPoint destination) => Unicast.Enqueue((message, destination));

        public void Deliver(byte[] message, IPEndPoint from) => _received!(message, from);

        public void Dispose()
        {
        }
    }
}
