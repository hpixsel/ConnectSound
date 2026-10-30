using ConnectSound.Core.Audio;

namespace ConnectSound.Core.Tests;

public class JitterBufferTests
{
    private static float[] Frame(float value) => Enumerable.Repeat(value, AudioFormat.FrameSamples).ToArray();

    [Fact]
    public void Waits_for_target_depth_before_playing()
    {
        var buffer = new JitterBuffer(targetFrames: 3, maxFrames: 10);
        var output = new float[AudioFormat.FrameSamples];

        buffer.Enqueue(Frame(1));
        buffer.Enqueue(Frame(2));
        Assert.False(buffer.TryDequeue(output));

        buffer.Enqueue(Frame(3));
        Assert.True(buffer.TryDequeue(output));
        Assert.Equal(1, output[0]);
    }

    [Fact]
    public void Rebuffers_after_running_dry()
    {
        var buffer = new JitterBuffer(targetFrames: 2, maxFrames: 10);
        var output = new float[AudioFormat.FrameSamples];
        buffer.Enqueue(Frame(1));
        buffer.Enqueue(Frame(2));

        Assert.True(buffer.TryDequeue(output));
        Assert.True(buffer.TryDequeue(output));
        Assert.False(buffer.TryDequeue(output));
        Assert.Equal(1, buffer.Underruns);

        buffer.Enqueue(Frame(3));
        Assert.False(buffer.TryDequeue(output));
    }

    [Fact]
    public void Drops_oldest_frames_when_sender_runs_ahead()
    {
        var buffer = new JitterBuffer(targetFrames: 2, maxFrames: 4);
        for (var i = 1; i <= 5; i++) buffer.Enqueue(Frame(i));

        Assert.Equal(2, buffer.Count);
        var output = new float[AudioFormat.FrameSamples];
        Assert.True(buffer.TryDequeue(output));
        Assert.Equal(4, output[0]);
    }
}

public class MixerTests
{
    private static void Feed(MixerChannel channel, float value, int frames = 4)
    {
        var frame = Enumerable.Repeat(value, AudioFormat.FrameSamples).ToArray();
        for (var i = 0; i < frames; i++) channel.Buffer.Enqueue(frame);
    }

    [Fact]
    public void Sums_channels_with_gain()
    {
        var mixer = new Mixer();
        var a = new MixerChannel("a") { Gain = 0.5f };
        var b = new MixerChannel("b");
        mixer.Add(a);
        mixer.Add(b);
        Feed(a, 0.2f);
        Feed(b, 0.3f);

        var output = new float[AudioFormat.FrameSamples];
        mixer.Read(output);

        Assert.Equal(0.4f, output[0], 4);
    }

    [Fact]
    public void Call_on_one_phone_ducks_media_from_others()
    {
        var mixer = new Mixer();
        var music = new MixerChannel("music") { Profile = SourceProfile.Media };
        var work = new MixerChannel("work") { Profile = SourceProfile.Calls, InCall = true };
        mixer.Add(music);
        mixer.Add(work);
        var output = new float[AudioFormat.FrameSamples];
        for (var i = 0; i < 100; i++)
        {
            Feed(music, 0.5f, frames: 1);
            Feed(work, 0f, frames: 1);
            mixer.Read(output);
        }

        Assert.True(mixer.CallActive);
        Assert.Equal(0.5f * Mixer.DuckLevel, output[0], 3);
    }

    [Fact]
    public void Muted_channel_is_metered_but_silent()
    {
        var mixer = new Mixer();
        var a = new MixerChannel("a") { Muted = true };
        mixer.Add(a);
        Feed(a, 0.5f);

        var output = new float[AudioFormat.FrameSamples];
        mixer.Read(output);

        Assert.All(output, s => Assert.Equal(0, s));
        Assert.True(a.Meter.Level > 0);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(3f)]
    [InlineData(-10f)]
    public void Soft_clip_stays_within_full_scale(float x)
    {
        var y = Mixer.SoftClip(x);
        Assert.InRange(y, -1f, 1f);
        Assert.Equal(Math.Sign(x), Math.Sign(y));
    }

    [Fact]
    public void Serves_any_buffer_size()
    {
        var mixer = new Mixer();
        var a = new MixerChannel("a");
        mixer.Add(a);
        Feed(a, 0.25f, frames: 10);

        var output = new float[441];
        for (var i = 0; i < 20; i++) mixer.Read(output);

        Assert.Equal(0.25f, output[^1], 4);
    }
}

public class ResamplerTests
{
    [Fact]
    public void Converts_44_1k_mono_to_48k_stereo_with_correct_length()
    {
        var resampler = new StereoResampler(44_100, 1);
        var input = Enumerable.Range(0, 44_100).Select(i => MathF.Sin(i * 0.01f)).ToArray();

        var total = 0;
        for (var offset = 0; offset < input.Length; offset += 441)
            total += resampler.Process(input.AsSpan(offset, 441)).Length;

        Assert.InRange(total / 2, 47_990, 48_000);
    }

    [Fact]
    public void Stereo_48k_passes_through()
    {
        var resampler = new StereoResampler(48_000, 2);
        Assert.True(resampler.IsPassthrough);
    }
}

public class OpusTests
{
    [Fact]
    public void Round_trips_a_tone()
    {
        var encoder = new OpusFrameEncoder();
        var decoder = new OpusFrameDecoder();
        var packet = new byte[OpusFrameEncoder.MaxPacketBytes];
        var decoded = new float[AudioFormat.FrameSamples];

        var energy = 0f;
        for (var f = 0; f < 10; f++)
        {
            var frame = new float[AudioFormat.FrameSamples];
            for (var i = 0; i < AudioFormat.FrameSamplesPerChannel; i++)
            {
                var s = 0.5f * MathF.Sin(2 * MathF.PI * 440 * (f * 960 + i) / 48_000f);
                frame[2 * i] = frame[2 * i + 1] = s;
            }

            var length = encoder.Encode(frame, packet);
            Assert.InRange(length, 1, OpusFrameEncoder.MaxPacketBytes);
            decoder.Decode(packet.AsSpan(0, length), decoded);
            energy = decoded.Sum(x => x * x) / decoded.Length;
        }

        Assert.InRange(energy, 0.08f, 0.17f); // a 0.5 amplitude sine has mean power 0.125
    }
}
