using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Backbloom.Audio.Windows;
using Backbloom.Core.Audio;
using TerraFX.Interop.Windows;

namespace Backbloom.Audio.Tests;

public sealed class AudioBlockWriterTests
{
    [Theory]
    [InlineData(48000, 2)]
    [InlineData(44100, 2)]
    [InlineData(22050, 1)]
    [InlineData(96000, 6)]
    public void Writer_ResamplesToBlocksOf22050HzMono(int rate, int channels)
    {
        var analysis = new MusicAnalysis();
        analysis.Start();
        var writer = new AudioBlockWriter(analysis);
        writer.Configure(new AudioFormat(rate, channels, AudioSampleFormat.IeeeFloat));
        var seconds = 2;
        var buffer = new float[rate / 100 * channels];
        long t = 0;
        for (var chunk = 0; chunk < seconds * 100; chunk++)
        {
            for (var f = 0; f < rate / 100; f++, t++)
            {
                for (var c = 0; c < channels; c++)
                {
                    buffer[(f * channels) + c] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * t / rate));
                }
            }

            writer.Write(MemoryMarshal.AsBytes(buffer.AsSpan()), rate / 100);
        }

        // what was written, at 22,050 Hz, in full blocks of 1024 samples (43 for 2 s)
        Assert.Equal(rate / 100 * 200 * (long)MusicAnalysis.SampleRate / rate / MusicAnalysis.BlockSize, writer.Blocks);
        Assert.Equal(0, writer.SilentBlocks);
    }

    [Fact]
    public void Writer_ConvertsIntegerFormats()
    {
        var analysis = new MusicAnalysis();
        analysis.Start();
        var writer = new AudioBlockWriter(analysis);

        // 16-bit at the native rate passes through unchanged (one channel)
        writer.Configure(new AudioFormat(MusicAnalysis.SampleRate, 1, AudioSampleFormat.Pcm16));
        var pcm = new byte[MusicAnalysis.BlockSize * 2];
        for (var i = 0; i < MusicAnalysis.BlockSize; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(i % 2 == 0 ? 1000 : -1000));
        }

        writer.Write(pcm, MusicAnalysis.BlockSize);
        Assert.Equal(1, writer.Blocks);

        // 24-bit stereo, one channel silent: the mean of both
        writer.Configure(new AudioFormat(MusicAnalysis.SampleRate, 2, AudioSampleFormat.Pcm24));
        var pcm24 = new byte[MusicAnalysis.BlockSize * 6];
        for (var i = 0; i < MusicAnalysis.BlockSize; i++)
        {
            var v = (i % 2 == 0 ? 2000 : -2000) << 8;
            pcm24[i * 6] = (byte)v;
            pcm24[(i * 6) + 1] = (byte)(v >> 8);
            pcm24[(i * 6) + 2] = (byte)(v >> 16);
        }

        writer.Write(pcm24, MusicAnalysis.BlockSize);
        Assert.Equal(2, writer.Blocks);

        // silence counts as silent blocks
        writer.WriteSilence(MusicAnalysis.BlockSize * 3);
        Assert.Equal((5, 3), (writer.Blocks, writer.SilentBlocks));
    }

    [Fact]
    public void Writer_DoesNotAllocatePerBuffer()
    {
        var analysis = new MusicAnalysis();
        analysis.Start();
        var writer = new AudioBlockWriter(analysis);
        writer.Configure(new AudioFormat(48000, 2, AudioSampleFormat.IeeeFloat));
        var buffer = new float[480 * 2];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (float)Math.Sin(i / 10.0) * 0.3f;
        }

        var bytes = MemoryMarshal.AsBytes(buffer.AsSpan()).ToArray();
        for (var i = 0; i < 300; i++)
        {
            writer.Write(bytes, 480);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 300; i++)
        {
            writer.Write(bytes, 480);
            writer.WriteSilence(100);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(writer.Blocks > 100);
    }
}

public sealed class SoundCaptureTests
{
    [Fact]
    public void Capture_RunsWhileHandlesAreOpenAndFollowsTheSettings()
    {
        var platform = new RecordingCapture();
        using var sound = new SoundCapture(platform, NullLogger<SoundCapture>.Instance);
        var changes = 0;
        sound.Changed += () => changes++;

        // disabled: a music effect gets no capture
        var first = sound.Open();
        Assert.Empty(platform.Sessions);
        Assert.False(sound.GetInfo().Active);

        sound.Configure(new SoundSettings(true, "default", true));
        Assert.Equal(["default"], platform.Sessions.Select(s => s.Device));
        Assert.True(sound.Analysis.Running);
        var second = sound.Open();
        Assert.Single(platform.Sessions);

        // a different device restarts; the old session ends first
        sound.Configure(new SoundSettings(true, "input:mic", false));
        Assert.Equal(["default", "input:mic"], platform.Sessions.Select(s => s.Device));
        Assert.True(platform.Sessions[0].Disposed);
        Assert.False(sound.Analysis.Smoothing);

        sound.Close(first);
        Assert.False(platform.Sessions[1].Disposed);
        sound.Close(second);
        Assert.True(platform.Sessions[1].Disposed);
        Assert.False(sound.Analysis.Running);

        // failures are reported as inactive, with the reason, until the device opens again
        sound.Open();
        platform.Sessions[^1].Observer.Failed("gone");
        Assert.False(sound.GetInfo().Active);
        Assert.Equal("gone", sound.GetInfo().Error);
        platform.Sessions[^1].Observer.Opened("Mic", new AudioFormat(48000, 1, AudioSampleFormat.IeeeFloat));
        var info = sound.GetInfo();
        Assert.True(info.Active);
        Assert.Null(info.Error);
        Assert.Equal("input:mic", info.Device);
        Assert.Equal(2, info.Devices.Count);
        Assert.True(changes >= 6);
    }

    [Fact]
    public void ReplacedSession_CannotMarkTheNewDeviceAsFailed()
    {
        var platform = new RecordingCapture();
        using var sound = new SoundCapture(platform, NullLogger<SoundCapture>.Instance);
        sound.Configure(new SoundSettings(true, "input:mic", true));
        sound.Open();
        var failing = platform.Sessions[0].Observer;
        failing.Failed("blocked");
        Assert.False(sound.GetInfo().Active);

        // switching devices clears the failure; the old session still reporting while it shuts down changes nothing
        sound.Configure(new SoundSettings(true, "default", true));
        failing.Failed("blocked");
        failing.Opened("Mic", new AudioFormat(48000, 1, AudioSampleFormat.IeeeFloat));
        Assert.True(sound.GetInfo().Active);
        platform.Sessions[1].Observer.Failed("lost");
        Assert.Equal("lost", sound.GetInfo().Error);
        failing.Opened("Mic", new AudioFormat(48000, 1, AudioSampleFormat.IeeeFloat));
        Assert.Equal("lost", sound.GetInfo().Error);
    }

    [Fact]
    public void Settings_ReadLikeTheCppBuild()
    {
        Assert.Equal(new SoundSettings(true, string.Empty, true), SoundSettings.FromJson(null));
        Assert.False(SoundSettings.FromJson(null).Active);
        Assert.Equal(new SoundSettings(false, "default", false), SoundSettings.FromJson(System.Text.Json.Nodes.JsonNode.Parse("""{ "enable": false, "device": " default ", "enable_smoothing": false }""")));
    }

    private sealed class RecordingCapture : IAudioCapture
    {
        public List<Session> Sessions { get; } = [];

        public string Name => "Recording";

        public IReadOnlyList<AudioDevice> GetDevices() =>
            [new(SoundSettings.DefaultDevice, "Default", AudioDeviceKind.Loopback), new("input:mic", "Mic", AudioDeviceKind.Microphone)];

        public IDisposable Start(string deviceId, AudioBlockWriter writer, IAudioCaptureObserver observer)
        {
            var session = new Session(deviceId, observer);
            Sessions.Add(session);
            return session;
        }

        public sealed class Session(string device, IAudioCaptureObserver observer) : IDisposable
        {
            public string Device => device;

            public IAudioCaptureObserver Observer => observer;

            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }
    }
}

/// <summary>
/// The real WASAPI capture on this PC (read-only: it only listens). The default output's loopback stream is
/// captured for a moment; while nothing plays, it delivers silence blocks.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed unsafe class WasapiTests
{
    [Fact]
    public void Devices_ListTheDefaultOutputFirstWithStableIds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WASAPI is Windows only.");
        var devices = new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance).GetDevices();
        Assert.Equal(SoundSettings.DefaultDevice, devices[0].Id);
        Assert.All(devices.Skip(1), d => Assert.True(d.Id.StartsWith("loopback:", StringComparison.Ordinal) || d.Id.StartsWith("input:", StringComparison.Ordinal)));
        Assert.Equal(devices.Count, devices.Select(d => d.Id).Distinct().Count());
        Assert.Equal(devices.Select(d => d.Id), new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance).GetDevices().Select(d => d.Id));
    }

    [Fact]
    public void MixFormats_AreParsed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WASAPI is Windows only.");
        var extensible = new WAVEFORMATEXTENSIBLE
        {
            Format = new WAVEFORMATEX { wFormatTag = WAVE.WAVE_FORMAT_EXTENSIBLE, nChannels = 2, nSamplesPerSec = 48000, wBitsPerSample = 32, cbSize = 22 },
            SubFormat = IID.IID_KSDATAFORMAT_SUBTYPE_IEEE_FLOAT,
        };
        Assert.Equal(new AudioFormat(48000, 2, AudioSampleFormat.IeeeFloat), WasapiSession.ParseFormat(&extensible.Format));
        var pcm = new WAVEFORMATEX { wFormatTag = WAVE.WAVE_FORMAT_PCM, nChannels = 1, nSamplesPerSec = 44100, wBitsPerSample = 16 };
        Assert.Equal(new AudioFormat(44100, 1, AudioSampleFormat.Pcm16), WasapiSession.ParseFormat(&pcm));
    }

    [Fact]
    public void MixFormats_OfOtherBitDepths_AreParsedOrRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WASAPI is Windows only.");
        var pcm24 = new WAVEFORMATEX { wFormatTag = WAVE.WAVE_FORMAT_PCM, nChannels = 2, nSamplesPerSec = 96000, wBitsPerSample = 24 };
        Assert.Equal(new AudioFormat(96000, 2, AudioSampleFormat.Pcm24), WasapiSession.ParseFormat(&pcm24));
        var extensible = new WAVEFORMATEXTENSIBLE
        {
            Format = new WAVEFORMATEX { wFormatTag = WAVE.WAVE_FORMAT_EXTENSIBLE, nChannels = 8, nSamplesPerSec = 48000, wBitsPerSample = 32, cbSize = 22 },
            SubFormat = IID.IID_KSDATAFORMAT_SUBTYPE_PCM,
        };
        Assert.Equal(new AudioFormat(48000, 8, AudioSampleFormat.Pcm32), WasapiSession.ParseFormat(&extensible.Format));

        var pcm8 = new WAVEFORMATEX { wFormatTag = WAVE.WAVE_FORMAT_PCM, nChannels = 1, nSamplesPerSec = 8000, wBitsPerSample = 8 };
        Assert.Equal("Unsupported mix format (tag 1, 8 bits)", Assert.Throws<AudioException>(() => Parse(pcm8)).Message);
        var float64 = new WAVEFORMATEX { wFormatTag = WAVE.WAVE_FORMAT_IEEE_FLOAT, nChannels = 2, nSamplesPerSec = 48000, wBitsPerSample = 64 };
        Assert.Throws<AudioException>(() => Parse(float64));
    }

    private static AudioFormat Parse(WAVEFORMATEX format) => WasapiSession.ParseFormat(&format);

    /// <summary>The failures a user can fix are explained in words; the UI shows them as the reason sound isn't captured.</summary>
    [Theory]
    [InlineData(0x80070005u, "Windows blocks access to the device; allow desktop apps to use the microphone in Settings")]
    [InlineData(0x88890004u, "The device was disconnected or disabled (Activate, 0x88890004)")]
    [InlineData(0x8889000Au, "Another app uses the device exclusively (Activate, 0x8889000A)")]
    [InlineData(0x80070490u, "The device isn't connected (Activate, 0x80070490)")]
    [InlineData(0x80004005u, "Activate failed (0x80004005)")]
    public void Failures_AreExplained(uint hresult, string message)
    {
        var failure = new AudioException("Activate", unchecked((int)hresult));

        Assert.StartsWith(message, failure.Message, StringComparison.Ordinal);
        Assert.Equal(unchecked((int)hresult), failure.HResult);
    }

    /// <summary>A configured device that isn't there anymore: the capture reports why and keeps trying, it doesn't crash.</summary>
    [Theory]
    [InlineData("input:{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}", "The device isn't connected")]
    [InlineData("loopback:{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}", "The device isn't connected")]
    [InlineData("A microphone that isn't there", "There is no recording device named 'A microphone that isn't there'")]
    public void Session_ForAMissingDevice_ReportsWhy(string device, string reason)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WASAPI is Windows only.");
        var analysis = new MusicAnalysis();
        var observer = new Observer();
        using (new WasapiSession(device, new AudioBlockWriter(analysis), observer, NullLogger.Instance))
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (observer.Failure is null && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
        }

        // Build servers often run without the Windows Audio service: no devices at all, and another error than this one.
        Assert.SkipWhen(
            observer.Failure?.StartsWith(reason, StringComparison.Ordinal) != true && !HasAudioDevices(),
            $"No sound devices on this machine: {observer.Failure ?? "no answer"}");
        Assert.StartsWith(reason, observer.Failure, StringComparison.Ordinal);
        Assert.Null(observer.Format);
    }

    private static bool HasAudioDevices()
    {
        try
        {
            return new WasapiAudioCapture(NullLogger<WasapiAudioCapture>.Instance).GetDevices().Count > 1;  // beyond the "default" entry
        }
        catch (Exception ex) when (ex is AudioException or COMException or InvalidOperationException)
        {
            return false;
        }
    }

    [Fact]
    public void Loopback_CapturesTheDefaultOutputWithoutAllocating()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WASAPI is Windows only.");
        var analysis = new MusicAnalysis();
        analysis.Start();
        var writer = new AudioBlockWriter(analysis);
        var observer = new Observer();
        using (var session = new WasapiSession(SoundSettings.DefaultDevice, writer, observer, NullLogger.Instance))
        {
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && (writer.Blocks < 40 || session.AllocatedWhileCapturing.Iterations < 30))
            {
                Thread.Sleep(50);
            }

            Assert.SkipWhen(observer.Failure is not null && observer.Format is null, $"No output device: {observer.Failure}");
            Assert.NotNull(observer.Format);

            // about 21.5 blocks per second, sound or silence
            Assert.True(writer.Blocks >= 40, $"{writer.Blocks} blocks");
            Assert.Equal(0, session.AllocatedWhileCapturing.Bytes);
        }
    }

    private sealed class Observer : IAudioCaptureObserver
    {
        public AudioFormat? Format { get; private set; }

        public string? Failure { get; private set; }

        public void Opened(string deviceName, AudioFormat format) => Format = format;

        public void Failed(string reason) => Failure = reason;
    }
}
