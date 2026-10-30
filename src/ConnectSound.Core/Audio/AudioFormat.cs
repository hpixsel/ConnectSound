namespace ConnectSound.Core.Audio;

/// <summary>
/// The one format that travels between devices. Every capture is converted to it
/// before encoding and every output is fed with it.
/// </summary>
public static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>20 ms per Opus frame.</summary>
    public const int FrameSamplesPerChannel = 960;
    public const int FrameSamples = FrameSamplesPerChannel * Channels;
    public const int FrameDurationMs = 20;
}

/// <summary>Captures what a device is playing, delivered as 48 kHz interleaved stereo floats.</summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Called on a capture thread. The span is only valid for the duration of the call.</summary>
    event SamplesHandler? SamplesCaptured;

    void Start();
    void Stop();
}

public delegate void SamplesHandler(ReadOnlySpan<float> interleavedStereo48k);

/// <summary>Something the output device pulls 48 kHz interleaved stereo floats from.</summary>
public interface ISampleProvider48k
{
    /// <summary>Fills the whole buffer. Never blocks; writes silence when nothing is available.</summary>
    void Read(Span<float> buffer);
}

public interface IAudioOutput : IDisposable
{
    void Start(ISampleProvider48k provider);
    void Stop();

    /// <summary>What keeps sound from playing right now (e.g. no speakers), in words for the user; null when playing.</summary>
    string? Problem { get; }
}

/// <summary>An audio device couldn't be opened. The message is meant for the user.</summary>
public sealed class AudioDeviceException(string message, Exception? inner = null) : Exception(message, inner);
