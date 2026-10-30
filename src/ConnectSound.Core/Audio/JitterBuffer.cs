namespace ConnectSound.Core.Audio;

/// <summary>
/// Queue of decoded 20 ms frames for one remote device. It prebuffers a few frames
/// before playing, and drops the oldest frames when the sender's clock runs ahead of ours,
/// so latency stays bounded instead of creeping up over a long session.
/// </summary>
public sealed class JitterBuffer
{
    private readonly Queue<float[]> _frames = new();
    private readonly Stack<float[]> _pool = new();
    private readonly Lock _lock = new();
    private bool _playing;

    public JitterBuffer(int targetFrames = 3, int maxFrames = 12)
    {
        if (targetFrames < 1 || maxFrames <= targetFrames) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        TargetFrames = targetFrames;
        MaxFrames = maxFrames;
    }

    public int TargetFrames { get; }
    public int MaxFrames { get; }

    public int Count { get { lock (_lock) return _frames.Count; } }

    public long Underruns { get; private set; }
    public long Dropped { get; private set; }

    public void Enqueue(ReadOnlySpan<float> frame)
    {
        if (frame.Length != AudioFormat.FrameSamples) throw new ArgumentException("Expected one 20 ms stereo frame.", nameof(frame));

        lock (_lock)
        {
            var buffer = _pool.Count > 0 ? _pool.Pop() : new float[AudioFormat.FrameSamples];
            frame.CopyTo(buffer);
            _frames.Enqueue(buffer);

            if (_frames.Count > MaxFrames)
            {
                while (_frames.Count > TargetFrames)
                {
                    _pool.Push(_frames.Dequeue());
                    Dropped++;
                }
            }
        }
    }

    /// <summary>Copies the next frame into <paramref name="destination"/>; returns false (and leaves it untouched) while buffering.</summary>
    public bool TryDequeue(Span<float> destination)
    {
        lock (_lock)
        {
            if (!_playing)
            {
                if (_frames.Count < TargetFrames) return false;
                _playing = true;
            }

            if (_frames.Count == 0)
            {
                _playing = false;
                Underruns++;
                return false;
            }

            var frame = _frames.Dequeue();
            frame.CopyTo(destination);
            _pool.Push(frame);
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            while (_frames.Count > 0) _pool.Push(_frames.Dequeue());
            _playing = false;
        }
    }
}
