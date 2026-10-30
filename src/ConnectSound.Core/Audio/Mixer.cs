namespace ConnectSound.Core.Audio;

public enum SourceProfile
{
    /// <summary>Music, videos, games. Gets turned down while a call is active.</summary>
    Media,

    /// <summary>The phone that handles calls. Its sound always stays on top.</summary>
    Calls,
}

/// <summary>One remote device as seen by the mixer.</summary>
public sealed class MixerChannel
{
    private float _duck = 1f;

    public MixerChannel(string id) => Id = id;

    public string Id { get; }
    public JitterBuffer Buffer { get; } = new();
    public LevelMeter Meter { get; } = new();

    public volatile float Gain = 1f;
    public volatile bool Muted;
    public volatile bool InCall;
    public volatile SourceProfile Profile;

    internal float NextDuck(float target)
    {
        // ~200 ms ramp so the music fades instead of cutting out.
        _duck += (target - _duck) * 0.1f;
        if (Math.Abs(target - _duck) < 0.001f) _duck = target;
        return _duck;
    }
}

/// <summary>
/// Sums every connected device into the host's output. Works in 20 ms frames and
/// hands the output device whatever buffer size it asks for.
/// </summary>
public sealed class Mixer : ISampleProvider48k
{
    public const float DuckLevel = 0.15f;

    private readonly Lock _lock = new();
    private MixerChannel[] _channels = [];
    private readonly float[] _mix = new float[AudioFormat.FrameSamples];
    private readonly float[] _scratch = new float[AudioFormat.FrameSamples];
    private int _mixOffset = AudioFormat.FrameSamples;

    public volatile float MasterGain = 1f;
    public LevelMeter MasterMeter { get; } = new();

    public bool CallActive { get; private set; }

    public void Add(MixerChannel channel)
    {
        lock (_lock) _channels = [.. _channels, channel];
    }

    public void Remove(MixerChannel channel)
    {
        lock (_lock) _channels = _channels.Where(c => c != channel).ToArray();
    }

    public void Read(Span<float> buffer)
    {
        while (!buffer.IsEmpty)
        {
            if (_mixOffset == _mix.Length)
            {
                MixNextFrame();
                _mixOffset = 0;
            }

            var take = Math.Min(buffer.Length, _mix.Length - _mixOffset);
            _mix.AsSpan(_mixOffset, take).CopyTo(buffer);
            _mixOffset += take;
            buffer = buffer[take..];
        }
    }

    private void MixNextFrame()
    {
        var channels = Volatile.Read(ref _channels);
        Array.Clear(_mix);

        var callActive = false;
        foreach (var c in channels)
            if (c.InCall && !c.Muted) callActive = true;
        CallActive = callActive;

        foreach (var channel in channels)
        {
            if (!channel.Buffer.TryDequeue(_scratch))
            {
                channel.Meter.Decay();
                continue;
            }

            channel.Meter.Feed(_scratch);
            if (channel.Muted) continue;

            var duckTarget = callActive && channel.Profile == SourceProfile.Media ? DuckLevel : 1f;
            var gain = channel.Gain * channel.NextDuck(duckTarget);
            for (var i = 0; i < _mix.Length; i++) _mix[i] += _scratch[i] * gain;
        }

        var master = MasterGain;
        for (var i = 0; i < _mix.Length; i++) _mix[i] = SoftClip(_mix[i] * master);
        MasterMeter.Feed(_mix);
    }

    /// <summary>Transparent below ~-3 dBFS, then bends smoothly towards ±1 instead of hard clipping.</summary>
    internal static float SoftClip(float x)
    {
        const float knee = 0.7f;
        var a = Math.Abs(x);
        if (a <= knee) return x;
        var over = (a - knee) / (1 - knee);
        var y = knee + (1 - knee) * (over / (1 + over));
        return MathF.CopySign(y, x);
    }
}
