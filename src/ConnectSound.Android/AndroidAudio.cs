using Android.Content;
using Android.Media;
using Android.Media.Projection;
using ConnectSound.Core.Audio;
using Application = Android.App.Application;
using CoreFormat = ConnectSound.Core.Audio.AudioFormat;
using DroidFormat = Android.Media.AudioFormat;

namespace ConnectSound.Android;

/// <summary>Records what other apps play, via AudioPlaybackCapture (Android 10+).</summary>
internal sealed class PlaybackCapture(MediaProjection projection) : IAudioCapture
{
    private AudioRecord? _record;
    private Thread? _thread;
    private volatile bool _running;

    public event SamplesHandler? SamplesCaptured;

    public void Start()
    {
        var config = new AudioPlaybackCaptureConfiguration.Builder(projection)
            .AddMatchingUsage(AudioUsageKind.Media)
            .AddMatchingUsage(AudioUsageKind.Game)
            .AddMatchingUsage(AudioUsageKind.Unknown)
            .Build();

        var format = new DroidFormat.Builder()
            .SetEncoding(Encoding.PcmFloat)!
            .SetSampleRate(CoreFormat.SampleRate)!
            // The builder is shared by input and output; the binding types the mask as ChannelOut.
            .SetChannelMask((ChannelOut)(int)ChannelIn.Stereo)!
            .Build()!;

        var minimum = AudioRecord.GetMinBufferSize(CoreFormat.SampleRate, ChannelIn.Stereo, Encoding.PcmFloat);
        _record = new AudioRecord.Builder()
            .SetAudioFormat(format)!
            .SetBufferSizeInBytes(Math.Max(minimum, CoreFormat.FrameSamples * sizeof(float) * 4))!
            .SetAudioPlaybackCaptureConfig(config)!
            .Build()!;

        try { _record.StartRecording(); }
        catch (Java.Lang.IllegalStateException e)
        {
            throw new AudioDeviceException("Android nie pozwolił rozpocząć przechwytywania dźwięku. Spróbuj połączyć się jeszcze raz.", e);
        }
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ConnectSound capture", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Loop()
    {
        global::Android.OS.Process.SetThreadPriority(global::Android.OS.ThreadPriority.UrgentAudio);
        var buffer = new float[CoreFormat.FrameSamples];
        while (_running)
        {
            var read = _record!.Read(buffer, 0, buffer.Length, 0 /* READ_BLOCKING */);
            if (read > 0) SamplesCaptured?.Invoke(buffer.AsSpan(0, read));
            else if (read < 0) break;
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _record?.Stop(); } catch (Java.Lang.IllegalStateException) { }
        _thread?.Join(500);
        _record?.Release();
        _record = null;
    }

    public void Dispose()
    {
        Stop();
        projection.Stop(); // also when capture never started, e.g. the host refused the connection
    }
}

/// <summary>Plays the mix through a low-latency AudioTrack on its own audio thread.</summary>
internal sealed class AudioTrackOutput : IAudioOutput
{
    public string? Problem => null;

    private AudioTrack? _track;
    private Thread? _thread;
    private volatile bool _running;

    public void Start(ISampleProvider48k provider)
    {
        var minimum = AudioTrack.GetMinBufferSize(CoreFormat.SampleRate, ChannelOut.Stereo, Encoding.PcmFloat);
        _track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Music)!
                .Build()!)
            .SetAudioFormat(new DroidFormat.Builder()
                .SetEncoding(Encoding.PcmFloat)!
                .SetSampleRate(CoreFormat.SampleRate)!
                .SetChannelMask(ChannelOut.Stereo)!
                .Build()!)
            .SetBufferSizeInBytes(Math.Max(minimum, CoreFormat.FrameSamples * sizeof(float) * 3))
            .SetTransferMode(AudioTrackMode.Stream)
            .SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)
            .Build();

        _track.Play();
        _running = true;
        _thread = new Thread(() => Loop(provider)) { IsBackground = true, Name = "ConnectSound output" };
        _thread.Start();
    }

    private void Loop(ISampleProvider48k provider)
    {
        global::Android.OS.Process.SetThreadPriority(global::Android.OS.ThreadPriority.UrgentAudio);
        var buffer = new float[CoreFormat.FrameSamples];
        while (_running)
        {
            provider.Read(buffer);
            _track!.Write(buffer, 0, buffer.Length, WriteMode.Blocking);
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(500);
        try { _track?.Stop(); } catch (Java.Lang.IllegalStateException) { }
        _track?.Release();
        _track = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Treats the phone as "in a call" whenever the audio system is in call or VoIP mode.
/// Works for cellular calls and apps like Teams or WhatsApp, and needs no phone-state permission.
/// </summary>
internal sealed class AudioModeCallMonitor : Core.Platform.ICallMonitor
{
    private readonly AudioManager _audio = (AudioManager)Application.Context.GetSystemService(Context.AudioService)!;
    private Timer? _timer;
    private bool _inCall;

    public bool InCall => _inCall;

    public event Action<bool>? InCallChanged;

    public void Start()
    {
        _inCall = Probe();
        _timer ??= new Timer(_ =>
        {
            var now = Probe();
            if (now == _inCall) return;
            _inCall = now;
            InCallChanged?.Invoke(now);
        }, null, 500, 500);
    }

    private bool Probe() => _audio.Mode is Mode.InCall or Mode.InCommunication or Mode.Ringtone;

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
