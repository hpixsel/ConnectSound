using Concentus;
using Concentus.Enums;

namespace ConnectSound.Core.Audio;

public sealed class OpusFrameEncoder
{
    public const int MaxPacketBytes = 1275;

    private readonly IOpusEncoder _encoder;

    public OpusFrameEncoder(int bitrate = 160_000)
    {
        _encoder = OpusCodecFactory.CreateEncoder(AudioFormat.SampleRate, AudioFormat.Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = bitrate;
        _encoder.Complexity = 6;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
    }

    /// <summary>Encodes one 20 ms frame; returns the packet length.</summary>
    public int Encode(ReadOnlySpan<float> frame, Span<byte> packet) =>
        _encoder.Encode(frame, AudioFormat.FrameSamplesPerChannel, packet, packet.Length);
}

public sealed class OpusFrameDecoder
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(AudioFormat.SampleRate, AudioFormat.Channels);

    public void Decode(ReadOnlySpan<byte> packet, Span<float> frame) =>
        _decoder.Decode(packet, frame, AudioFormat.FrameSamplesPerChannel);

    /// <summary>Packet loss concealment: synthesises a plausible frame for a packet that never arrived.</summary>
    public void Conceal(Span<float> frame) =>
        _decoder.Decode(ReadOnlySpan<byte>.Empty, frame, AudioFormat.FrameSamplesPerChannel);
}
