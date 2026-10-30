namespace ConnectSound.Core.Audio;

/// <summary>
/// Collects arbitrarily sized capture callbacks into fixed 20 ms frames.
/// Not thread-safe: feed it from one capture thread.
/// </summary>
public sealed class FrameAccumulator
{
    private readonly float[] _frame = new float[AudioFormat.FrameSamples];
    private int _filled;

    public event Action<float[]>? FrameReady;

    public void Push(ReadOnlySpan<float> samples)
    {
        while (!samples.IsEmpty)
        {
            var take = Math.Min(samples.Length, _frame.Length - _filled);
            samples[..take].CopyTo(_frame.AsSpan(_filled));
            _filled += take;
            samples = samples[take..];

            if (_filled == _frame.Length)
            {
                FrameReady?.Invoke(_frame);
                _filled = 0;
            }
        }
    }

    public void Reset() => _filled = 0;
}

/// <summary>
/// Linear-interpolating resampler with channel up/down mixing to stereo.
/// Good enough for program audio; keeps state between calls so there are no clicks at block edges.
/// </summary>
public sealed class StereoResampler
{
    private readonly int _inChannels;
    private readonly double _step;
    private double _position;
    private float _lastL, _lastR;
    private float[] _out = [];

    public StereoResampler(int inputSampleRate, int inputChannels)
    {
        if (inputChannels < 1) throw new ArgumentOutOfRangeException(nameof(inputChannels));
        _inChannels = inputChannels;
        _step = (double)inputSampleRate / AudioFormat.SampleRate;
    }

    public bool IsPassthrough => _step == 1.0 && _inChannels == 2;

    /// <summary>Returns a span over an internal buffer, valid until the next call.</summary>
    public ReadOnlySpan<float> Process(ReadOnlySpan<float> input)
    {
        if (IsPassthrough) return input;

        var inFrames = input.Length / _inChannels;
        var maxOut = (int)Math.Ceiling((inFrames + 1) / _step) + 2;
        if (_out.Length < maxOut * 2) _out = new float[maxOut * 2];

        var written = 0;
        // _position is measured in input frames, where -1 refers to the last frame of the previous block.
        while (true)
        {
            var index = (int)Math.Floor(_position);
            if (index + 1 >= inFrames) break;

            var frac = (float)(_position - index);
            var (l0, r0) = index < 0 ? (_lastL, _lastR) : ReadFrame(input, index);
            var (l1, r1) = ReadFrame(input, index + 1);
            _out[written++] = l0 + (l1 - l0) * frac;
            _out[written++] = r0 + (r1 - r0) * frac;
            _position += _step;
        }

        if (inFrames > 0)
        {
            (_lastL, _lastR) = ReadFrame(input, inFrames - 1);
            _position -= inFrames;
        }

        return _out.AsSpan(0, written);
    }

    private (float L, float R) ReadFrame(ReadOnlySpan<float> input, int frame)
    {
        var o = frame * _inChannels;
        return _inChannels == 1 ? (input[o], input[o]) : (input[o], input[o + 1]);
    }
}

/// <summary>Peak meter with a fast attack and a slow, console-like release.</summary>
public sealed class LevelMeter
{
    private float _level;

    /// <summary>0..1, already shaped for display (roughly -48 dB..0 dB).</summary>
    public float Level => Volatile.Read(ref _level);

    public void Feed(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }

        var shaped = ToDisplay(peak);
        var current = Volatile.Read(ref _level);
        Volatile.Write(ref _level, shaped > current ? shaped : current * 0.86f + shaped * 0.14f);
    }

    public void Decay() => Volatile.Write(ref _level, Volatile.Read(ref _level) * 0.8f);

    internal static float ToDisplay(float peak)
    {
        if (peak <= 0.0039f) return 0; // below -48 dB
        var db = 20 * MathF.Log10(peak);
        return Math.Clamp((db + 48f) / 48f, 0f, 1f);
    }
}
