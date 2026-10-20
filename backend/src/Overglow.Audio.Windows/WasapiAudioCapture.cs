using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.AUDCLNT_SHAREMODE;
using static TerraFX.Interop.Windows.CLSCTX;
using static TerraFX.Interop.Windows.Windows;

namespace Overglow.Audio.Windows;

/// <summary>
/// Sound capture with WASAPI in shared mode: an output device in loopback mode ("what you hear": music, games and
/// videos playing on this PC, the main use of music mode) or a recording device, in the device's mix format
/// (usually 48 kHz float stereo), converted by <see cref="AudioBlockWriter"/>. HyperHDR used waveIn, which has no
/// loopback. Interop: TerraFX.Interop.Windows, as the screen capture (allocation-free COM calls, AOT-compatible).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCapture(ILogger<WasapiAudioCapture> logger) : IAudioCapture
{
    public string Name => "WASAPI";

    public IReadOnlyList<AudioDevice> GetDevices() => WasapiDevices.List();

    public IDisposable Start(string deviceId, AudioBlockWriter writer, IAudioCaptureObserver observer)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(observer);
        return new WasapiSession(deviceId, writer, observer, logger);
    }
}

/// <summary>
/// One capture on its own thread: event-driven reads (every 50 ms at the latest), silence while a loopback device
/// delivers nothing (nothing plays), reopening when the device is lost or (for the default device) when Windows
/// switches the default output. Each buffer is handed to the writer as a span over the WASAPI buffer: no copies, no
/// allocations per buffer.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WasapiSession : IDisposable
{
    private const long BufferDuration = 2_000_000; // 200 ms in 100 ns units
    private const uint WaitMs = 50;
    private const long DefaultCheckMs = 2000;
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(1);

    private readonly string _deviceId;
    private readonly AudioBlockWriter _writer;
    private readonly IAudioCaptureObserver _observer;
    private readonly ILogger _logger;
    private const int WarmUpIterations = 20;

    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private long _allocatedWhileCapturing;
    private long _measuredIterations;

    public WasapiSession(string deviceId, AudioBlockWriter writer, IAudioCaptureObserver observer, ILogger logger)
    {
        _deviceId = deviceId;
        _writer = writer;
        _observer = observer;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "Sound capture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Bytes the capture thread allocated in its read loop after a short warm-up (diagnostics; must stay 0), and the
    /// loop iterations measured.
    /// </summary>
    internal (long Bytes, long Iterations) AllocatedWhileCapturing => (Interlocked.Read(ref _allocatedWhileCapturing), Interlocked.Read(ref _measuredIterations));

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }

    internal static AudioFormat ParseFormat(WAVEFORMATEX* format)
    {
        var tag = format->wFormatTag;
        var bits = format->wBitsPerSample;
        var isFloat = tag == WAVE.WAVE_FORMAT_IEEE_FLOAT;
        var isPcm = tag == WAVE.WAVE_FORMAT_PCM;
        if (tag == WAVE.WAVE_FORMAT_EXTENSIBLE && format->cbSize >= 22)
        {
            var sub = ((WAVEFORMATEXTENSIBLE*)format)->SubFormat;
            isFloat = sub == IID.IID_KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            isPcm = sub == IID.IID_KSDATAFORMAT_SUBTYPE_PCM;
        }

        AudioSampleFormat sampleFormat = (isFloat, isPcm, bits) switch
        {
            (true, _, 32) => AudioSampleFormat.IeeeFloat,
            (_, true, 16) => AudioSampleFormat.Pcm16,
            (_, true, 24) => AudioSampleFormat.Pcm24,
            (_, true, 32) => AudioSampleFormat.Pcm32,
            _ => throw new AudioException($"Unsupported mix format (tag {tag}, {bits} bits)"),
        };
        return new AudioFormat((int)format->nSamplesPerSec, format->nChannels, sampleFormat);
    }

    private void Run()
    {
        var entered = WasapiDevices.EnterCom();
        var audioEvent = CreateEventW(null, FALSE, FALSE, null);
        try
        {
            while (!_stop.WaitOne(0))
            {
                bool reopenAtOnce;
                try
                {
                    reopenAtOnce = CaptureUntilStopped(audioEvent);
                }
                catch (AudioException ex)
                {
                    _observer.Failed(ex.Message);
                    reopenAtOnce = false;
                }

                if (!reopenAtOnce && _stop.WaitOne(s_retryDelay))
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogCrashed(ex);
            _observer.Failed(ex.Message);
        }
        finally
        {
            _ = CloseHandle(audioEvent);
            WasapiDevices.LeaveCom(entered);
        }
    }

    // Opens the device and captures until stopped (false) or the default output changed (true: reopen now).
    private bool CaptureUntilStopped(HANDLE audioEvent)
    {
        IMMDeviceEnumerator* enumerator = null;
        IMMDevice* device = null;
        IAudioClient* client = null;
        IAudioCaptureClient* capture = null;
        WAVEFORMATEX* mix = null;
        try
        {
            enumerator = WasapiDevices.CreateEnumerator();
            device = WasapiDevices.Resolve(enumerator, _deviceId, out var loopback, out var name);
            var endpointId = WasapiDevices.EndpointId(device) ?? string.Empty;
            var iid = IID.IID_IAudioClient;
            WasapiDevices.Check(device->Activate(&iid, (uint)CLSCTX_ALL, null, (void**)&client), "IMMDevice.Activate");
            WasapiDevices.Check(client->GetMixFormat(&mix), "GetMixFormat");
            var format = ParseFormat(mix);
            var flags = AUDCLNT.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | (loopback ? AUDCLNT.AUDCLNT_STREAMFLAGS_LOOPBACK : 0);
            WasapiDevices.Check(client->Initialize(AUDCLNT_SHAREMODE_SHARED, (uint)flags, BufferDuration, 0, mix, null), "IAudioClient.Initialize");
            WasapiDevices.Check(client->SetEventHandle(audioEvent), "SetEventHandle");
            var captureIid = IID.IID_IAudioCaptureClient;
            WasapiDevices.Check(client->GetService(&captureIid, (void**)&capture), "GetService(IAudioCaptureClient)");
            _writer.Configure(format);
            WasapiDevices.Check(client->Start(), "IAudioClient.Start");
            _observer.Opened(loopback ? $"{name} (loopback)" : name, format);
            return Capture(enumerator, capture, format, loopback, endpointId, audioEvent);
        }
        finally
        {
            if (client is not null)
            {
                _ = client->Stop();
            }

            CoTaskMemFree(mix);
            WasapiDevices.Release(ref capture);
            WasapiDevices.Release(ref client);
            WasapiDevices.Release(ref device);
            WasapiDevices.Release(ref enumerator);
        }
    }

    private bool Capture(IMMDeviceEnumerator* enumerator, IAudioCaptureClient* capture, AudioFormat format, bool loopback, string endpointId, HANDLE audioEvent)
    {
        var handles = stackalloc HANDLE[2];
        handles[0] = (HANDLE)_stop.SafeWaitHandle.DangerousGetHandle();
        handles[1] = audioEvent;
        var followDefault = _deviceId == SoundSettings.DefaultDevice;
        var lastData = Stopwatch.GetTimestamp();
        var nextDefaultCheck = Environment.TickCount64 + DefaultCheckMs;
        var allocatedBefore = 0L;
        for (var iteration = 0; ; iteration++)
        {
            if (iteration == WarmUpIterations)
            {
                allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            }
            else if (iteration > WarmUpIterations)
            {
                Interlocked.Exchange(ref _allocatedWhileCapturing, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                Interlocked.Increment(ref _measuredIterations);
            }

            if (WaitForMultipleObjects(2, handles, FALSE, WaitMs) == WAIT.WAIT_OBJECT_0)
            {
                return false;
            }

            if (Drain(capture, format) > 0)
            {
                lastData = Stopwatch.GetTimestamp();
            }
            else if (loopback)
            {
                // a loopback stream delivers nothing while nothing plays: that is silence, at the device's rate
                var silentFrames = (long)(Stopwatch.GetElapsedTime(lastData).TotalSeconds * format.SampleRate);
                if (silentFrames >= format.SampleRate / 20)
                {
                    _writer.WriteSilence((int)silentFrames);
                    lastData += silentFrames * Stopwatch.Frequency / format.SampleRate;
                }
            }

            if (followDefault && Environment.TickCount64 >= nextDefaultCheck)
            {
                nextDefaultCheck = Environment.TickCount64 + DefaultCheckMs;
                if (!WasapiDevices.IsDefaultOutput(enumerator, endpointId))
                {
                    LogDefaultChanged();
                    return true;
                }
            }
        }
    }

    // Hands every waiting packet to the writer; returns the frames read.
    private int Drain(IAudioCaptureClient* capture, AudioFormat format)
    {
        var total = 0;
        while (true)
        {
            uint packet;
            WasapiDevices.Check(capture->GetNextPacketSize(&packet), "GetNextPacketSize");
            if (packet == 0)
            {
                return total;
            }

            byte* data;
            uint frames;
            uint flags;
            WasapiDevices.Check(capture->GetBuffer(&data, &frames, &flags, null, null), "GetBuffer");
            if ((flags & (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
            {
                _writer.WriteSilence((int)frames);
            }
            else
            {
                _writer.Write(new ReadOnlySpan<byte>(data, (int)frames * format.BytesPerFrame), (int)frames);
            }

            WasapiDevices.Check(capture->ReleaseBuffer(frames), "ReleaseBuffer");
            total += (int)frames;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The default output device changed; sound capture follows it")]
    private partial void LogDefaultChanged();

    [LoggerMessage(Level = LogLevel.Error, Message = "Sound capture stopped unexpectedly")]
    private partial void LogCrashed(Exception exception);
}
