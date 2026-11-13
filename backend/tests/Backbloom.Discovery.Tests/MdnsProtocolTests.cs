using System.Net;
using Backbloom.Discovery.Mdns;
using Backbloom.Tests.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Backbloom.Discovery.Tests;

/// <summary>
/// mDNS edge cases: malformed and hostile messages, record lifetimes, responders that answer only in part, and how
/// the responder replies to the different kinds of queriers.
/// </summary>
public sealed class MdnsProtocolTests
{
    private const string Instance = "wled-desk._wled._tcp.local.";

    private static readonly IPAddress s_wled = IPAddress.Parse("10.0.0.40");
    private static readonly IPEndPoint s_wledPeer = new(s_wled, UdpMdnsTransport.MdnsPort);
    private static readonly MdnsAdvertisement s_webUi = new(KnownServices.WebUi, "MY-PC:18290", "MY-PC.local.", 18290, ["version=0.1.0"], [IPAddress.Parse("10.0.0.2")]);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>A response header with one answer, followed by <paramref name="hex"/> (spaces ignored).</summary>
    private static byte[] ResponseWith(string hex) =>
        [0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, .. Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal))];

    [Theory]
    [InlineData("")] // the answer is missing
    [InlineData("C00C 0001 0001 00000078 0000")] // the name points at itself (a hostile loop)
    [InlineData("C0")] // a name pointer is cut off
    [InlineData("05 6162")] // a label is longer than the rest of the message
    [InlineData("00 0001")] // the class is missing
    [InlineData("01 61 00 0001 0001 00000078 000A 0A00")] // the data is shorter than its length
    [InlineData("01 61 00 0021 0001 00000078 0002 0000")] // an SRV record shorter than its fields
    public void Decode_RejectsMalformedMessages(string answer) =>
        Assert.Throws<InvalidDataException>(() => DnsMessage.Decode(ResponseWith(answer)));

    [Fact]
    public void Decode_RejectsAMessageShorterThanItsHeader() =>
        Assert.Throws<InvalidDataException>(() => DnsMessage.Decode(new byte[11]));

    [Fact]
    public void Records_KeepIpv6AddressesAndUnknownTypesAsTheyAre()
    {
        var v6 = IPAddress.Parse("fe80::1");
        var message = new DnsMessage
        {
            IsResponse = true,
            Answers =
            [
                new DnsRecord("desk.local.", DnsType.Aaaa, 120, v6),
                new DnsRecord("desk.local.", (DnsType)47, 120, new byte[] { 1, 2, 3 }),
                // an A record of the wrong length isn't an address
                new DnsRecord("desk.local.", DnsType.A, 120, new byte[] { 10, 0, 0 }),
            ],
        };

        var decoded = DnsMessage.Decode(message.Encode());

        Assert.Equal(v6, decoded.Answers[0].Data);
        Assert.Equal([1, 2, 3], (byte[])decoded.Answers[1].Data);
        Assert.Equal([10, 0, 0], (byte[])decoded.Answers[2].Data);
    }

    [Fact]
    public void Encode_CutsTxtStringsAt255Bytes_AndRejectsWhatDnsCantCarry()
    {
        var txt = new DnsMessage { Answers = [new DnsRecord("i.local.", DnsType.Txt, 120, new[] { new string('x', 300), "b=1" })] };
        Assert.Equal([new string('x', 255), "b=1"], (IEnumerable<string>)DnsMessage.Decode(txt.Encode()).Answers[0].Data);

        var longLabel = DnsMessage.Query(new DnsQuestion(new string('a', 64) + ".local.", DnsType.A));
        Assert.Throws<ArgumentException>(() => longLabel.Encode());
        var unsupported = new DnsMessage { Answers = [new DnsRecord("i.local.", DnsType.A, 120, 42)] };
        Assert.Throws<ArgumentException>(() => unsupported.Encode());
    }

    [Fact]
    public void Handle_IgnoresDatagramsThatArentDns()
    {
        var transport = new FakeTransport();
        using var mdns = new MdnsService(() => transport, () => transport, NullLogger<MdnsService>.Instance, new FakeTimeProvider());
        mdns.Advertise(s_webUi);
        var sent = transport.Multicast.Count;

        mdns.Handle([1, 2, 3], s_wledPeer);
        mdns.Handle(ResponseWith("C0"), s_wledPeer);

        Assert.Equal(sent, transport.Multicast.Count);
        Assert.Empty(transport.Unicast);
    }

    private static byte[] Answer(params DnsRecord[] records) => new DnsMessage { IsResponse = true, Answers = records }.Encode();

    private static DnsRecord Ptr(uint ttl = 120) => new(KnownServices.Wled, DnsType.NamePointer, ttl, Instance);

    private static DnsRecord Srv(uint ttl = 120) => new(Instance, DnsType.Srv, ttl, new SrvData(0, 0, 80, "wled-desk.local."));

    private static DnsRecord HostA(IPAddress address, uint ttl = 120) => new("wled-desk.local.", DnsType.A, ttl, address);

    [Fact]
    public async Task Browse_AsksForMissingHostAddresses_AndReportsTheTxtRecords()
    {
        var transport = new FakeTransport();
        var time = new SteppingClock();
        using var mdns = new MdnsService(() => transport, () => transport, NullLogger<MdnsService>.Instance, time, _ => false);
        // the device answers the PTR query with PTR, SRV and TXT only, and the address query with its A record
        transport.OnSend = query =>
        {
            var question = DnsMessage.Decode(query).Questions[0];
            transport.Deliver(
                question.Type == DnsType.A
                    ? Answer(HostA(IPAddress.Parse("10.0.0.41")))
                    : Answer(Ptr(), Srv(), new DnsRecord(Instance, DnsType.Txt, 120, (List<string>)["mac=aabbcc"])),
                s_wledPeer);
        };

        var found = Assert.Single(await time.RunAsync(mdns.BrowseAsync(KnownServices.Wled, TimeSpan.FromMilliseconds(500), Cancel)));

        Assert.Equal(IPAddress.Parse("10.0.0.41"), found.Address);
        Assert.Equal(["mac=aabbcc"], found.Txt);
        var addressQuery = DnsMessage.Decode(transport.Multicast.Last());
        Assert.Equal(new DnsQuestion("wled-desk.local.", DnsType.A, UnicastResponse: true), Assert.Single(addressQuery.Questions));
    }

    [Fact]
    public async Task Browse_FallsBackToTheAnsweringAddress_WhenTheHostAddressNeverComes()
    {
        var transport = new FakeTransport();
        var time = new SteppingClock();
        using var mdns = new MdnsService(() => transport, () => transport, NullLogger<MdnsService>.Instance, time, _ => false);
        transport.OnSend = query =>
        {
            if (DnsMessage.Decode(query).Questions[0].Type == DnsType.NamePointer)
            {
                transport.Deliver(Answer(Ptr(), Srv()), s_wledPeer);
            }
        };

        var found = Assert.Single(await time.RunAsync(mdns.BrowseAsync(KnownServices.Wled, TimeSpan.FromMilliseconds(500), Cancel)));

        Assert.Equal(s_wled, found.Address);
        Assert.Equal("wled-desk.local", found.HostName);
    }

    [Fact]
    public async Task Browse_LongerThanASecond_AsksAgainHalfway()
    {
        var transport = new FakeTransport();
        var time = new SteppingClock();
        using var mdns = new MdnsService(() => transport, () => transport, NullLogger<MdnsService>.Instance, time, _ => false);
        var started = time.GetUtcNow();

        Assert.Empty(await time.RunAsync(mdns.BrowseAsync(KnownServices.Wled, TimeSpan.FromSeconds(3), Cancel)));

        Assert.Equal(2, transport.Multicast.Count);
        Assert.All(transport.Multicast, q => Assert.Equal(DnsType.NamePointer, DnsMessage.Decode(q).Questions[0].Type));
        Assert.Equal(TimeSpan.FromSeconds(3), time.GetUtcNow() - started);
    }

    [Fact]
    public async Task Browse_ForgetsRecordsWhenTheirTtlRunsOut()
    {
        var transport = new FakeTransport();
        var time = new SteppingClock();
        using var mdns = new MdnsService(() => transport, () => transport, NullLogger<MdnsService>.Instance, time, _ => false);
        mdns.Handle(Answer(Ptr(ttl: 4500), Srv(ttl: 10), HostA(s_wled, ttl: 10)), s_wledPeer);

        Assert.Single(await time.RunAsync(mdns.BrowseAsync(KnownServices.Wled, TimeSpan.FromMilliseconds(100), Cancel)));

        // the pointer still lives, the SRV record doesn't
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(await time.RunAsync(mdns.BrowseAsync(KnownServices.Wled, TimeSpan.FromMilliseconds(100), Cancel)));
    }

    [Fact]
    public async Task Browse_ForgetsAHostAddressSaidGoodbyeTo()
    {
        var transport = new FakeTransport();
        var time = new SteppingClock();
        using var mdns = new MdnsService(() => transport, () => transport, NullLogger<MdnsService>.Instance, time, _ => false);
        mdns.Handle(Answer(Ptr(), Srv(), HostA(IPAddress.Parse("10.0.0.41"))), s_wledPeer);
        mdns.Handle(Answer(HostA(IPAddress.Parse("10.0.0.41"), ttl: 0)), s_wledPeer);

        var found = Assert.Single(await time.RunAsync(mdns.BrowseAsync(KnownServices.Wled, TimeSpan.FromMilliseconds(100), Cancel)));

        // without the A record the instance is reported at the address its SRV record came from
        Assert.Equal(s_wled, found.Address);
    }

    [Fact]
    public async Task Browse_NeverReportsThisComputer()
    {
        var transport = new FakeTransport();
        var time = new SteppingClock();
        // the default own-address check: loopback is always this computer
        using var mdns = new MdnsService(() => transport, () => transport, NullLogger<MdnsService>.Instance, time);
        mdns.Handle(Answer(Ptr(), Srv(), HostA(IPAddress.Loopback)), new IPEndPoint(IPAddress.Loopback, UdpMdnsTransport.MdnsPort));

        Assert.Empty(await time.RunAsync(mdns.BrowseAsync(KnownServices.Wled, TimeSpan.FromMilliseconds(100), Cancel)));
        Assert.Null(await time.RunAsync(mdns.ResolveAsync("wled-desk.local", TimeSpan.FromMilliseconds(100), Cancel)));
    }

    [Fact]
    public void Responder_AnswersMulticastQueriersByMulticast_AndQuBitQueriersDirectly()
    {
        var transport = new FakeTransport();
        using var mdns = new MdnsService(() => transport, () => transport, NullLogger<MdnsService>.Instance, new FakeTimeProvider());
        mdns.Advertise(s_webUi);
        var phone = new IPEndPoint(IPAddress.Parse("10.0.0.50"), UdpMdnsTransport.MdnsPort);
        var before = transport.Multicast.Count;

        // a querier on port 5353 without the QU bit gets a multicast answer
        mdns.Handle(DnsMessage.Query(new DnsQuestion(KnownServices.WebUi, DnsType.NamePointer)).Encode(), phone);
        Assert.Equal(before + 1, transport.Multicast.Count);
        Assert.Empty(transport.Unicast);

        // with the QU bit it gets the same answer directly, without echoing the question
        mdns.Handle(DnsMessage.Query(new DnsQuestion(s_webUi.InstanceName, DnsType.Txt, UnicastResponse: true)).Encode(), phone);
        var (reply, to) = Assert.Single(transport.Unicast);
        Assert.Equal(phone, to);
        var decoded = DnsMessage.Decode(reply);
        Assert.Empty(decoded.Questions);
        Assert.Equal(DnsType.Txt, Assert.Single(decoded.Answers).Type);

        // questions about other services get no answer
        mdns.Handle(DnsMessage.Query(new DnsQuestion(KnownServices.Wled, DnsType.NamePointer, UnicastResponse: true)).Encode(), phone);
        Assert.Single(transport.Unicast);
        Assert.Equal(before + 1, transport.Multicast.Count);
    }

    [Fact]
    public void Responder_StaysQuietWhileNothingIsAdvertised()
    {
        var transport = new FakeTransport();
        using var mdns = new MdnsService(() => transport, () => transport, NullLogger<MdnsService>.Instance, new FakeTimeProvider());
        mdns.Advertise(s_webUi);
        mdns.StopAdvertising();
        mdns.StopAdvertising();
        var sent = transport.Multicast.Count;

        mdns.Handle(DnsMessage.Query(new DnsQuestion(KnownServices.WebUi, DnsType.NamePointer, UnicastResponse: true)).Encode(), s_wledPeer);

        Assert.Equal(sent, transport.Multicast.Count);
        Assert.Empty(transport.Unicast);
    }

    [Fact]
    public void Advertise_AgainReplacesTheAdvertisement_OnTheSameResponder()
    {
        var transport = new FakeTransport();
        var opened = 0;
        using var mdns = new MdnsService(() => { opened++; return transport; }, () => new FakeTransport(), NullLogger<MdnsService>.Instance, new FakeTimeProvider());
        mdns.Advertise(s_webUi);

        Assert.True(mdns.Advertise(s_webUi with { Port = 18291 }));

        Assert.Equal(1, opened);
        var srv = DnsMessage.Decode(transport.Multicast.Last()).Answers.Single(r => r.Type == DnsType.Srv);
        Assert.Equal(18291, ((SrvData)srv.Data).Port);
        var reply = MdnsService.Respond(s_webUi with { Port = 18291 }, new DnsQuestion(s_webUi.InstanceName, DnsType.Srv))!;
        Assert.Equal([DnsType.Txt, DnsType.A], reply.Additionals.Select(r => r.Type));
    }

    [Fact]
    public void Advertise_SurvivesAResponderThatCantSend()
    {
        var transport = new FakeTransport { SendFailure = new ObjectDisposedException("socket") };
        using var mdns = new MdnsService(() => transport, () => new FakeTransport(), NullLogger<MdnsService>.Instance, new FakeTimeProvider());

        Assert.True(mdns.Advertise(s_webUi));
        mdns.StopAdvertising();

        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public void Respond_AnswersAddressQuestionsOnlyWithAddresses()
    {
        var withoutAddresses = s_webUi with { Addresses = [] };

        Assert.Null(MdnsService.Respond(withoutAddresses, new DnsQuestion("MY-PC.local.", DnsType.A)));
        Assert.Null(MdnsService.Respond(s_webUi, new DnsQuestion("MY-PC.local.", DnsType.Txt)));
        Assert.Null(MdnsService.Respond(s_webUi, new DnsQuestion(s_webUi.InstanceName, DnsType.A)));
        var any = MdnsService.Respond(s_webUi, new DnsQuestion("my-pc.local", DnsType.Any))!;
        Assert.Equal(IPAddress.Parse("10.0.0.2"), Assert.Single(any.Answers).Data);
    }

    [Fact]
    public async Task Resolve_StopsWhenCanceled()
    {
        var transport = new FakeTransport();
        var time = new SteppingClock();
        using var mdns = new MdnsService(() => transport, () => transport, NullLogger<MdnsService>.Instance, time, _ => false);
        using var cancel = new CancellationTokenSource();

        var resolving = mdns.ResolveAsync("wled-desk.local", TimeSpan.FromSeconds(5), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving);
        Assert.Equal(1, transport.Disposals);
    }
}
