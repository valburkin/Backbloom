using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Backbloom.Audio;
using Backbloom.Capture;
using Backbloom.Core.Color;
using Backbloom.Core.Effects;
using Backbloom.Core.Imaging;
using Backbloom.Core.Layout;
using Backbloom.Core.Mapping;
using Backbloom.Devices;
using Backbloom.Engine.Platform;
using Backbloom.Settings;
using Backbloom.Settings.Storage;

namespace Backbloom.Engine.Tests;

/// <summary>A clock whose waits return at once with the time moved to the deadline.</summary>
internal sealed class SteppingClock(long start = 1000) : IClock
{
    private long _now = start;

    public long NowMilliseconds => Interlocked.Read(ref _now);

    public void SleepUntil(long dueMilliseconds)
    {
        if (dueMilliseconds > NowMilliseconds)
        {
            Interlocked.Exchange(ref _now, dueMilliseconds);
        }
    }
}

/// <summary>A screen that delivers solid frames until told to stop (lock screen, monitor off).</summary>
internal sealed class SwitchableSource : IFrameSource
{
    public volatile bool Delivering = true;

    public string Name => "Switchable";

    public string Device => "test";

    public (int Width, int Height) FrameSize => (32, 18);

    public bool TryCapture(Frame frame, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(10) || !Delivering)
        {
            return false;
        }

        MemoryMarshal.Cast<byte, uint>(frame.Pixels).Fill(0xff204060u);
        return true;
    }

    public void Dispose()
    {
    }
}

/// <summary>Sound capture that plays a beat (48 kHz float stereo) in real time and counts its sessions.</summary>
internal sealed class FakeAudioCapture : IAudioCapture
{
    private int _started;
    private int _running;

    public string Name => "Fake";

    public int Started => Volatile.Read(ref _started);

    public int Running => Volatile.Read(ref _running);

    public string? LastDevice { get; private set; }

    public IReadOnlyList<AudioDevice> GetDevices() =>
        [new(SoundSettings.DefaultDevice, "Default output (what you hear)", AudioDeviceKind.Loopback), new("input:mic", "Microphone", AudioDeviceKind.Microphone)];

    public IDisposable Start(string deviceId, AudioBlockWriter writer, IAudioCaptureObserver observer)
    {
        LastDevice = deviceId;
        Interlocked.Increment(ref _started);
        Interlocked.Increment(ref _running);
        return new Session(this, writer, observer);
    }

    private sealed class Session : IDisposable
    {
        private readonly FakeAudioCapture _owner;
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;

        public Session(FakeAudioCapture owner, AudioBlockWriter writer, IAudioCaptureObserver observer)
        {
            _owner = owner;
            var format = new AudioFormat(48000, 2, AudioSampleFormat.IeeeFloat);
            writer.Configure(format);
            observer.Opened("Fake", format);
            _thread = new Thread(() =>
            {
                var buffer = new float[960 * 2];
                var t = 0L;
                while (!_stop.Token.WaitHandle.WaitOne(20))
                {
                    for (var i = 0; i < 960; i++, t++)
                    {
                        // a 60 Hz bass that pulses twice a second, with some treble
                        var envelope = t % 24000 < 8000 ? 0.8 : 0.1;
                        var v = (float)((envelope * Math.Sin(2 * Math.PI * 60 * t / 48000.0)) + (0.05 * Math.Sin(2 * Math.PI * 5000 * t / 48000.0)));
                        buffer[i * 2] = buffer[(i * 2) + 1] = v;
                    }

                    writer.Write(MemoryMarshal.AsBytes(buffer.AsSpan()), 960);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _thread.Join();
            _stop.Dispose();
            Interlocked.Decrement(ref _owner._running);
        }
    }
}

public sealed class EffectEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"backbloom-effects-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _owned = [];
    private ISettingsStore? _store;

    public void Dispose()
    {
        _owned.ForEach(d => d.Dispose());
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void EffectSource_StepsAtTheEffectsTimesWithoutAllocating()
    {
        var layout = LedLayout.FromJson(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "layouts", "owner-168.json"))));
        var mapping = new LedMappingOptions(layout);
        var colors = new Vector3[layout.Count];
        foreach (var name in new[] { "Rainbow swirl fast", "Candle", "Breath", "Warm mood blobs" })
        {
            var clock = new SteppingClock();
            using var source = new EffectSource(EffectCatalog.Find(name)!, 50, -1, clock, new MsvcRandom(3), null);
            for (var i = 0; i < 200; i++)
            {
                Assert.True(source.TryRender(mapping, colors, CancellationToken.None));
            }

            var start = clock.NowMilliseconds;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 300; i++)
            {
                Assert.True(source.TryRender(mapping, colors, CancellationToken.None));
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            var stepMs = (clock.NowMilliseconds - start) / 300.0;
            switch (name)
            {
                case "Rainbow swirl fast":
                    // 7 s per turn, 360 steps: 19 ms (Animation_Swirl::getSTime)
                    Assert.Equal(19, stepMs, 1);
                    Assert.Contains(colors, c => c.X > 0.5f);
                    break;
                case "Candle":
                    // asks for 200 ms; Effect::run waited at most 100 ms
                    Assert.Equal(100, stepMs, 1);
                    break;
                case "Warm mood blobs":
                    Assert.Equal(100, stepMs, 1);
                    break;
            }
        }
    }

    [Fact]
    public void EffectSource_ReturnsAtOnceWhenCanceled()
    {
        // Deterministic: the clock cancels during the first wait slice (a real timer can fire late when the thread pool
        // is busy). Warm mood blobs steps every 100 ms, so without the cancellation check the render would take five
        // 20 ms slices before drawing.
        using var cts = new CancellationTokenSource();
        var clock = new CancelingClock(cts);
        using var source = new EffectSource(EffectCatalog.Find("Warm mood blobs")!, 50, -1, clock, new MsvcRandom(3), null);
        var mapping = new LedMappingOptions(LedLayout.FromJson(JsonNode.Parse("""[{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }, { "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }]""")));
        var colors = new Vector3[2];
        Assert.True(source.TryRender(mapping, colors, CancellationToken.None));
        clock.CancelOnNextSleep = true;

        Assert.False(source.TryRender(mapping, colors, cts.Token));
        Assert.Equal(1, clock.SleepsAfterArming);
    }

    /// <summary>A stepping clock that cancels a token during the first wait after being armed.</summary>
    private sealed class CancelingClock(CancellationTokenSource cts) : IClock
    {
        private long _now = 1000;

        public bool CancelOnNextSleep { get; set; }

        public int SleepsAfterArming { get; private set; }

        public long NowMilliseconds => _now;

        public void SleepUntil(long dueMilliseconds)
        {
            _now = Math.Max(_now, dueMilliseconds);
            if (CancelOnNextSleep)
            {
                SleepsAfterArming++;
                cts.Cancel();
            }
        }
    }

    [Fact]
    public void Effects_RunReplaceExpireAndClear()
    {
        var engine = Engine(new SolidSource(0, 0, 255));
        engine.Start();

        Assert.False(engine.RunEffect("No such effect", 50, -1, "test"));
        Assert.True(engine.RunEffect("Candle", 50, -1, "test@peer"));
        var effect = engine.Priorities[0];
        Assert.Equal(("EFFECT", "Candle", "test@peer", true), (effect.ComponentId, effect.Owner, effect.Origin, effect.Visible));
        Assert.Equal(new ActiveEffect("Candle", 50, -1), Assert.Single(engine.Effects.Active));

        // the candle's orange reaches the preview (blue screen underneath)
        var preview = new byte[3];
        WaitUntil(() => engine.LedColors.CopyLatest(preview) == 3 && preview[0] > preview[2]);

        // another effect at the same priority replaces it; one with a duration expires by itself
        Assert.True(engine.RunEffect("Breath", 50, -1, "test"));
        Assert.Equal("Breath", Assert.Single(engine.Effects.Active).Name);
        Assert.True(engine.RunEffect("Rainbow swirl", 40, 400, "test"));
        Assert.Equal(40, engine.Priorities[0].Priority);
        WaitUntil(() => engine.Effects.Active.Count == 1 && engine.Priorities[0].Priority == 50);

        // a color at its priority ends an effect, and so does clear
        engine.SetColor(50, new Rgb8(1, 2, 3), 0, "test");
        WaitUntil(() => engine.Effects.Active.Count == 0);
        Assert.True(engine.RunEffect("Breath", 60, -1, "test"));
        Assert.True(engine.Clear(-1));
        WaitUntil(() => engine.Effects.Active.Count == 0 && engine.Priorities is [{ ComponentId: "SYSTEMGRABBER" }]);
    }

    [Fact]
    public void StartupEffect_RunsForItsDurationOverTheScreen()
    {
        var engine = Engine(new SolidSource(0, 0, 255), """{ "foregroundEffect": { "enable": true, "type": "effect", "color": [255, 0, 0], "effect": "Rainbow swirl fast", "duration_ms": 600 } }""");
        engine.Start();
        Assert.Equal(new ActiveEffect("Rainbow swirl fast", 0, 600), Assert.Single(engine.Effects.Active));
        Assert.Equal((0, "EFFECT"), (engine.Priorities[0].Priority, engine.Priorities[0].ComponentId));
        WaitUntil(() => engine.Effects.Active.Count == 0 && engine.Priorities is [{ ComponentId: "SYSTEMGRABBER", Visible: true }]);
    }

    [Fact]
    public void StartupEffect_WaitsWhileTheOutputIsHeld_AndRunsWhenItIsReleased()
    {
        var engine = Engine(new SolidSource(0, 0, 255), """{ "foregroundEffect": { "enable": true, "type": "effect", "color": [255, 0, 0], "effect": "Rainbow swirl fast", "duration_ms": 600 } }""");
        engine.Components.SetHeld(true);
        engine.Start();

        // the first-run welcome (or HyperHDR) holds the lights: no startup effect, no controller opened
        Thread.Sleep(300);
        Assert.Empty(engine.Effects.Active);
        Assert.All(engine.Devices, d => Assert.Equal(DeviceState.Off, d.State));
        Assert.True(engine.Components.IsOn(Component.LedDevice));

        engine.Components.SetHeld(false);
        Assert.Equal(new ActiveEffect("Rainbow swirl fast", 0, 600), Assert.Single(engine.Effects.Active));
        WaitUntil(() => engine.Devices.All(d => d.State == DeviceState.Ready));

        // held again (HyperHDR started): the controllers are switched off, the startup effect doesn't repeat
        engine.Components.SetHeld(true);
        WaitUntil(() => engine.Devices.All(d => d.State == DeviceState.Off));
        WaitUntil(() => engine.Effects.Active.Count == 0);
        engine.Components.SetHeld(false);
        Assert.Empty(engine.Effects.Active);
    }

    [Fact]
    public void StartupColor_IsShownForItsDuration()
    {
        var engine = Engine(new SolidSource(0, 0, 255), """{ "foregroundEffect": { "enable": true, "type": "color", "color": [255, 0, 0], "effect": "Rainbow swirl fast", "duration_ms": 0 } }""");
        engine.Start();

        // "forever" is not allowed for a startup effect: 3 s
        var color = engine.Priorities[0];
        Assert.Equal((0, "COLOR", new Rgb8(255, 0, 0)), (color.Priority, color.ComponentId, color.Color));
        Assert.InRange(color.DurationMs!.Value, 2500, 3000);
    }

    [Fact]
    public void BackgroundEffect_ShowsWhileTheScreenDeliversNothing()
    {
        var screen = new SwitchableSource();
        var engine = Engine(screen, """{ "backgroundEffect": { "enable": true, "type": "effect", "color": [255, 138, 0], "effect": "Breath" } }""");
        engine.Start();
        WaitUntil(() => engine.Priorities is [{ ComponentId: "SYSTEMGRABBER", Visible: true }, { Priority: 254, ComponentId: "EFFECT" }]);

        // HyperHDR's rule: 800 ms without frames and the screen is inactive, so the background effect shows
        screen.Delivering = false;
        var sw = Stopwatch.StartNew();
        WaitUntil(() => engine.Priorities is [{ Priority: 254, Visible: true }, ..]);
        Assert.InRange(sw.ElapsedMilliseconds, 700, 2500);
        Assert.False(engine.Priorities.Single(p => p.ComponentId == "SYSTEMGRABBER").Active);

        // the screen is probed while hidden and comes back with its first frame
        screen.Delivering = true;
        WaitUntil(() => engine.Priorities is [{ ComponentId: "SYSTEMGRABBER", Visible: true }, ..]);

        // the background effect is not an "active effect" for the UI, survives "clear all", and goes with its setting
        Assert.True(engine.Clear(-1));
        Assert.Contains(engine.Priorities, p => p.Priority == 254);
        var store = _store!;
        var background = store.GetSection("backgroundEffect")!.AsObject();
        background["enable"] = false;
        Assert.True(store.Update(new JsonObject { ["backgroundEffect"] = background.DeepClone() }).Success);
        WaitUntil(() => engine.Priorities is [{ ComponentId: "SYSTEMGRABBER" }] && engine.Effects.Active.Count == 0);
    }

    [Fact]
    public void MusicEffect_RunsTheSoundCaptureWhileItRuns()
    {
        var platform = new FakeAudioCapture();
        var sound = new SoundCapture(platform, NullLogger<SoundCapture>.Instance);
        _owned.Add(sound);
        var engine = Engine(new SolidSource(0, 0, 0), """{ "soundEffect": { "enable": true, "device": "default", "enable_smoothing": true } }""", sound);
        engine.Start();
        Assert.Equal(0, platform.Started);

        Assert.True(engine.RunEffect(MusicPulseEffect.Name, 50, -1, "test"));
        Assert.Equal((1, 1, "default"), (platform.Started, platform.Running, platform.LastDevice));
        var preview = new byte[3];
        WaitUntil(() => sound.Blocks > 10 && engine.LedColors.CopyLatest(preview) == 3 && preview.Any(b => b > 10));
        Assert.True(sound.GetInfo() is { Active: true, Running: true, Device: "default", Devices.Count: 2 });

        // a second music effect shares the capture; it stops with the last one
        Assert.True(engine.RunEffect(MusicWavesEffect.Name, 40, -1, "test"));
        Assert.Equal(1, platform.Started);
        engine.Clear(40);
        Assert.Equal(1, platform.Running);
        engine.Clear(50);
        WaitUntil(() => platform.Running == 0);

        // a device change while a music effect runs restarts the capture on the new device
        Assert.True(engine.RunEffect(MusicPulseEffect.Name, 50, -1, "test"));
        var store = _store!;
        Assert.True(store.Update(JsonNode.Parse("""{ "soundEffect": { "enable": true, "device": "input:mic", "enable_smoothing": false } }""")!.AsObject()).Success);
        Assert.Equal((3, 1, "input:mic"), (platform.Started, platform.Running, platform.LastDevice));
        Assert.False(sound.Analysis.Smoothing);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException();
            }

            Thread.Sleep(10);
        }
    }

    private LightingEngine Engine(IFrameSource source, string? settings = null, SoundCapture? sound = null)
    {
        var registry = new DriverRegistry().RegisterAll();
        var database = SettingsDatabase.Open(Path.Combine(_dir, SettingsDatabase.FileName));
        _owned.Add(database);
        var store = new SqliteSettingsStore(database, SettingsSchema.CreateBuiltin(registry.Drivers.ToDictionary(d => d.Type, d => d.Schema)), NullLogger<SqliteSettingsStore>.Instance);
        _store = store;
        var sections = JsonNode.Parse(settings ?? "{}")!.AsObject();
        if (!sections.ContainsKey("foregroundEffect"))
        {
            var foreground = store.GetSection("foregroundEffect")!.AsObject();
            foreground["enable"] = false;
            sections["foregroundEffect"] = foreground.DeepClone();
        }

        sections["leds"] = JsonNode.Parse("""[{ "hmin": 0, "hmax": 0.5, "vmin": 0, "vmax": 1 }, { "hmin": 0.5, "hmax": 1, "vmin": 0, "vmax": 1 }]""");
        sections["device"] = JsonNode.Parse("""{ "type": "file", "output": "\\\\.\\NUL", "printTimeStamp": false }""");
        var result = store.Update(sections);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        var devices = new DeviceManager(registry, new DriverContext(NullLoggerFactory.Instance, new HttpClient()), NullLoggerFactory.Instance);
        var engine = new LightingEngine(store, source, devices, new SystemClock(), NullLoggerFactory.Instance, sound);
        _owned.Insert(0, engine);
        return engine;
    }
}
