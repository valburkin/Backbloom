namespace Backbloom.Audio;

/// <summary>What a sound device captures.</summary>
public enum AudioDeviceKind
{
    /// <summary>"What you hear": the mix an output device plays (music, games, videos on this PC).</summary>
    Loopback,

    /// <summary>A recording device (microphone, line in): the sound in the room.</summary>
    Microphone,
}

/// <summary>A sound device that can be selected in <c>soundEffect.device</c>.</summary>
/// <param name="Id">Stable id stored in the settings (survives restarts and renaming the device).</param>
/// <param name="Name">Name for the UI.</param>
public sealed record AudioDevice(string Id, string Name, AudioDeviceKind Kind);

/// <summary>Layout of the samples a capture delivers (interleaved channels).</summary>
public enum AudioSampleFormat
{
    /// <summary>32-bit IEEE float, -1..1.</summary>
    IeeeFloat,
    Pcm16,
    Pcm24,
    Pcm32,
}

/// <param name="SampleRate">Frames per second.</param>
/// <param name="Channels">Interleaved channels per frame.</param>
public sealed record AudioFormat(int SampleRate, int Channels, AudioSampleFormat SampleFormat)
{
    public int BytesPerSample => SampleFormat switch
    {
        AudioSampleFormat.Pcm16 => 2,
        AudioSampleFormat.Pcm24 => 3,
        _ => 4,
    };

    public int BytesPerFrame => BytesPerSample * Channels;
}

/// <summary>What a capture session reports about its device.</summary>
public interface IAudioCaptureObserver
{
    /// <summary>The device was opened and delivers <paramref name="format"/> (also after a reopen).</summary>
    void Opened(string deviceName, AudioFormat format);

    /// <summary>The device couldn't be opened or was lost; the session retries by itself.</summary>
    void Failed(string reason);
}

/// <summary>
/// The platform's sound capture (WASAPI on Windows): lists the devices and captures one of them on its own thread
/// into an <see cref="AudioBlockWriter"/>.
/// </summary>
public interface IAudioCapture
{
    /// <summary>Name for the logs, e.g. "WASAPI".</summary>
    string Name { get; }

    /// <summary>The devices that can be captured now; <see cref="SoundSettings.DefaultDevice"/> is always first.</summary>
    IReadOnlyList<AudioDevice> GetDevices();

    /// <summary>
    /// Starts capturing <paramref name="deviceId"/> (an id from <see cref="GetDevices"/>, or a device name from older
    /// settings) on a dedicated thread. The session reopens the device when it is lost and follows the default
    /// output device for <see cref="SoundSettings.DefaultDevice"/>; disposing it stops the capture.
    /// </summary>
    IDisposable Start(string deviceId, AudioBlockWriter writer, IAudioCaptureObserver observer);
}
