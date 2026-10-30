using System.Runtime.InteropServices;
using ConnectSound.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace ConnectSound.Desktop;

/// <summary>Captures everything the default playback device plays (WASAPI loopback).</summary>
internal sealed class LoopbackCapture : IAudioCapture
{
    private WasapiLoopbackCapture? _capture;
    private StereoResampler? _resampler;
    private float[] _floats = [];

    public event SamplesHandler? SamplesCaptured;

    public void Start()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            var format = _capture.WaveFormat;
            _resampler = new StereoResampler(format.SampleRate, format.Channels);
            _capture.DataAvailable += OnData;
            _capture.StartRecording();
        }
        catch (COMException e)
        {
            _capture?.Dispose();
            _capture = null;
            throw new AudioDeviceException("Ten komputer nie ma aktywnego urządzenia odtwarzającego, więc nie ma czego przechwycić. Włącz głośniki lub słuchawki.", e);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var format = _capture!.WaveFormat;
        var samples = ToFloats(e.Buffer.AsSpan(0, e.BytesRecorded), format);
        if (samples.IsEmpty) return;
        SamplesCaptured?.Invoke(_resampler!.Process(samples));
    }

    private ReadOnlySpan<float> ToFloats(ReadOnlySpan<byte> bytes, WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                      || format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;

        if (isFloat && format.BitsPerSample == 32) return MemoryMarshal.Cast<byte, float>(bytes);

        var count = bytes.Length / (format.BitsPerSample / 8);
        if (_floats.Length < count) _floats = new float[count];
        switch (format.BitsPerSample)
        {
            case 16:
                var shorts = MemoryMarshal.Cast<byte, short>(bytes);
                for (var i = 0; i < count; i++) _floats[i] = shorts[i] / 32768f;
                break;
            case 24:
                for (var i = 0; i < count; i++)
                    _floats[i] = ((bytes[3 * i] << 8 | bytes[3 * i + 1] << 16 | bytes[3 * i + 2] << 24) >> 8) / 8388608f;
                break;
            case 32:
                var ints = MemoryMarshal.Cast<byte, int>(bytes);
                for (var i = 0; i < count; i++) _floats[i] = ints[i] / 2147483648f;
                break;
            default:
                return default;
        }
        return _floats.AsSpan(0, count);
    }

    public void Stop()
    {
        if (_capture is null) return;
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch (COMException) { }
        _capture.Dispose();
        _capture = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Plays the mix on the default playback device through WASAPI shared mode. Follows the default
/// device when the user switches it (or unplugs headphones) and waits for one if there is none.
/// </summary>
internal sealed class WasapiOutput : IAudioOutput, IMMNotificationClient
{
    private readonly Lock _lock = new();
    private MMDeviceEnumerator? _enumerator;
    private ISampleProvider48k? _provider;
    private WasapiOut? _out;
    private Timer? _retry;
    private volatile string? _problem;

    public string? Problem => _problem;

    public void Start(ISampleProvider48k provider)
    {
        _provider = provider;
        _enumerator = new MMDeviceEnumerator();
        _enumerator.RegisterEndpointNotificationCallback(this);
        _retry = new Timer(_ => Open());
        Open();
    }

    private void Open()
    {
        lock (_lock)
        {
            if (_provider is null) return;
            Close();
            try
            {
                var device = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 60);
                output.Init(new Adapter(_provider));
                output.PlaybackStopped += OnStopped;
                output.Play();
                _out = output;
                _problem = null;
            }
            catch (COMException)
            {
                _problem = "Nie widzę głośników ani słuchawek. Podłącz urządzenie odtwarzające. ConnectSound zacznie grać sam.";
                _retry?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        // Device unplugged or invalidated: try the (new) default again shortly.
        if (e.Exception is not null && sender == _out) _retry?.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        if (sender == _out) _out = null;
    }

    private void Close()
    {
        var output = _out;
        _out = null;
        if (output is null) return;
        output.PlaybackStopped -= OnStopped;
        try { output.Stop(); } catch (COMException) { }
        output.Dispose();
    }

    public void Stop()
    {
        lock (_lock)
        {
            _provider = null;
            _retry?.Dispose();
            _retry = null;
            Close();
            if (_enumerator is not null)
            {
                _enumerator.UnregisterEndpointNotificationCallback(this);
                _enumerator.Dispose();
                _enumerator = null;
            }
        }
    }

    public void Dispose() => Stop();

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Called on a COM thread; reopening there can deadlock, so hop to the timer.
        if (flow == DataFlow.Render && role == Role.Multimedia) _retry?.Change(TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) { }
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) { }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    private sealed class Adapter(ISampleProvider48k provider) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, AudioFormat.Channels);

        public int Read(byte[] buffer, int offset, int count)
        {
            count -= count % 8; // whole stereo float frames
            provider.Read(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count)));
            return count;
        }
    }
}

/// <summary>Starts the preferred capture and switches to the fallback if the OS can't provide it.</summary>
internal sealed class FallbackCapture(IAudioCapture preferred, IAudioCapture fallback) : IAudioCapture
{
    private IAudioCapture? _active;

    public event SamplesHandler? SamplesCaptured;

    public void Start()
    {
        try
        {
            Use(preferred);
        }
        catch (AudioDeviceException)
        {
            Use(fallback);
        }
    }

    private void Use(IAudioCapture capture)
    {
        capture.SamplesCaptured += Forward;
        try { capture.Start(); }
        catch { capture.SamplesCaptured -= Forward; throw; }
        _active = capture;
    }

    private void Forward(ReadOnlySpan<float> samples) => SamplesCaptured?.Invoke(samples);

    public void Stop()
    {
        if (_active is null) return;
        _active.SamplesCaptured -= Forward;
        _active.Stop();
        _active = null;
    }

    public void Dispose()
    {
        Stop();
        preferred.Dispose();
        fallback.Dispose();
    }
}
