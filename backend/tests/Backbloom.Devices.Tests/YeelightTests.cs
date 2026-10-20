using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Backbloom.Devices.Drivers;
using Backbloom.Devices.Drivers.Yeelight;

namespace Backbloom.Devices.Tests;

/// <summary>Yeelight commands and music mode against a fake lamp on loopback (no real lamp is contacted).</summary>
public sealed class YeelightTests
{
    [Fact]
    public void Commands_HaveSortedKeysLikeQt() =>
        Assert.Equal("""{"id":7,"method":"set_scene","params":["color",65280,100,"smooth",50]}""", YeelightLight.Command(7, "set_scene", ["color", 65280, 100, "smooth", 50]));

    [Theory]
    [InlineData(255, 0, 0, 0, 255, 255)]
    [InlineData(0, 128, 0, 120, 255, 128)]
    [InlineData(10, 10, 10, 0, 0, 10)]
    [InlineData(255, 128, 0, 30, 255, 255)]
    public void Hsv_MatchesColorRgbGetHsv(byte r, byte g, byte b, int hue, int sat, int value) =>
        Assert.Equal((hue, sat, value), LampColor.RgbToHsv(r, g, b));

    [Fact]
    public async Task Driver_PowersOnStreamsInMusicModeAndPowersOff()
    {
        await using var lamp = new FakeLamp();
        var options = new YeelightOptions([("127.0.0.1", lamp.Port, "lamp")], QuotaWait: TimeSpan.Zero, ResponseSilence: TimeSpan.FromMilliseconds(30));
        using var driver = new YeelightDriver(options, Ctx.Create(), () => IPAddress.Loopback);

        await driver.OpenAsync(1, TestContext.Current.CancellationToken);
        Assert.True(driver.Write([0, 255, 0]));
        Assert.True(driver.Write([0, 255, 0])); // same color: nothing is sent
        Wait.Until(() => lamp.Stream.Count == 1);
        await driver.CloseAsync(TestContext.Current.CancellationToken);

        var musicPort = JsonNode.Parse(lamp.Control[2])!["params"]![2]!.GetValue<int>();
        Assert.Equal(
        [
            """{"id":1,"method":"set_music","params":[0]}""",
            """{"id":2,"method":"set_power","params":["on","smooth",5000,2]}""",
            $$"""{"id":3,"method":"set_music","params":[1,"127.0.0.1",{{musicPort}}]}""",
            """{"id":5,"method":"set_music","params":[0]}""",
            """{"id":6,"method":"set_power","params":["off","smooth",1000,2]}""",
        ], lamp.Control);
        Assert.Equal(["""{"id":4,"method":"set_scene","params":["color",65280,100,"smooth",50]}"""], lamp.Stream);
        Assert.Null(driver.EffectiveRefreshInterval(TimeSpan.FromMilliseconds(40)));
    }

    [Fact]
    public async Task Brightness_BelowTheMinimumSwitchesOff()
    {
        await using var lamp = new FakeLamp();
        var options = new YeelightOptions([("127.0.0.1", lamp.Port, "lamp")], BrightnessMin: 10, ExtraTimeDarknessMs: 200, QuotaWait: TimeSpan.Zero, ResponseSilence: TimeSpan.FromMilliseconds(30));
        using var driver = new YeelightDriver(options, Ctx.Create(), () => IPAddress.Loopback);

        await driver.OpenAsync(1, TestContext.Current.CancellationToken);
        driver.Write([0, 0, 10]);
        Wait.Until(() => lamp.Stream.Count == 1);

        Assert.Equal("""{"id":4,"method":"set_scene","params":["color",10,0,"smooth",250]}""", lamp.Stream.Single());
    }

    [Fact]
    public async Task GetProperties_ReadsEveryProperty()
    {
        await using var lamp = new FakeLamp(method => method == "get_prop" ? """["on","50","4000","65280"]""" : """["ok"]""");
        var context = Ctx.Create();

        var properties = await YeelightDriver.GetPropertiesAsync(Ctx.Request(context, $$"""{ "hostname": "127.0.0.1", "port": {{lamp.Port}} }"""));

        Assert.Equal("""{"power":"on","bright":"50","ct":"4000","rgb":"65280"}""", properties.ToJsonString());
        Assert.StartsWith("""{"id":1,"method":"get_prop","params":["power","bright","ct","rgb","hue","sat",""", lamp.Control.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_FailsWhenNoLampAnswers()
    {
        var unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        var port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        using var driver = new YeelightDriver(new YeelightOptions([("127.0.0.1", port, "x")]), Ctx.Create(), () => IPAddress.Loopback);

        await Assert.ThrowsAsync<IOException>(async () => await driver.OpenAsync(1, TestContext.Current.CancellationToken));
    }

    /// <summary>Answers every command with a result; on <c>set_music [1, ip, port]</c> it connects back like a lamp.</summary>
    private sealed class FakeLamp : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string, string> _result;
        private readonly Task _loop;
        private readonly List<TcpClient> _clients = [];

        public FakeLamp(Func<string, string>? result = null)
        {
            _result = result ?? (_ => """["ok"]""");
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Control { get; } = [];

        public ConcurrentQueue<string> Stream { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            foreach (var c in _clients)
            {
                c.Dispose();
            }

            try
            {
                await _loop;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }

            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _clients.Add(client);
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            var writer = client.GetStream();
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                lock (Control)
                {
                    Control.Add(line);
                }

                var command = JsonNode.Parse(line)!;
                var method = command["method"]!.GetValue<string>();
                await writer.WriteAsync(Encoding.UTF8.GetBytes($$"""{"id":{{command["id"]}},"result":{{_result(method)}}}""" + "\r\n"), _stop.Token);
                if (method == "set_music" && command["params"]![0]!.GetValue<int>() == 1)
                {
                    var stream = new TcpClient();
                    await stream.ConnectAsync(command["params"]![1]!.GetValue<string>(), command["params"]![2]!.GetValue<int>(), _stop.Token);
                    _clients.Add(stream);
                    _ = Task.Run(async () =>
                    {
                        using var r = new StreamReader(stream.GetStream(), Encoding.UTF8);
                        while (await r.ReadLineAsync(_stop.Token) is { } scene)
                        {
                            Stream.Enqueue(scene);
                        }
                    });
                }
            }
        }
    }
}
