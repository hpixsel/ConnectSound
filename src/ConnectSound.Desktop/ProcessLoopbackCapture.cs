using System.Runtime.InteropServices;
using ConnectSound.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace ConnectSound.Desktop;

/// <summary>
/// Captures what every app plays except ConnectSound itself, using WASAPI process loopback
/// (Windows 10 2004+). Unlike endpoint loopback it taps the apps' streams before the speakers'
/// volume and mute, so the PC can be muted locally while its sound still goes to the host.
/// </summary>
internal sealed class ProcessLoopbackCapture : IAudioCapture
{
    private const string VirtualProcessLoopbackDevice = "VAD\\Process_Loopback";
    private const int BufferDuration100Ns = 200 * 10_000;

    // Process loopback can't report a mix format; we ask for 48 kHz 16-bit stereo and let WASAPI convert.
    private static readonly WaveFormat Format = new(AudioFormat.SampleRate, 16, AudioFormat.Channels);

    private AudioClient? _client;
    private AutoResetEvent? _ready;
    private Thread? _thread;
    private volatile bool _running;
    private float[] _floats = [];
    private short[] _pcm = [];

    public event SamplesHandler? SamplesCaptured;

    /// <summary>Throws <see cref="AudioDeviceException"/> when the OS doesn't support process loopback.</summary>
    public void Start()
    {
        try
        {
            _client = new AudioClient(Activate(Environment.ProcessId));
            _client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                BufferDuration100Ns, 0, Format, Guid.Empty);
            _ready = new AutoResetEvent(false);
            _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
            _client.Start();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or EntryPointNotFoundException or DllNotFoundException or TimeoutException)
        {
            Stop();
            throw new AudioDeviceException("Ta wersja Windows nie obsługuje przechwytywania dźwięku aplikacji.", e);
        }

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ConnectSound process loopback", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Loop()
    {
        var capture = _client!.AudioCaptureClient;
        while (_running)
        {
            if (!_ready!.WaitOne(100)) continue;
            int packet;
            while (_running && (packet = capture.GetNextPacketSize()) > 0)
            {
                var data = capture.GetBuffer(out var frames, out var flags);
                var count = frames * AudioFormat.Channels;
                if (_floats.Length < count) _floats = new float[count];

                if ((flags & AudioClientBufferFlags.Silent) != 0)
                {
                    Array.Clear(_floats, 0, count);
                }
                else
                {
                    if (_pcm.Length < count) _pcm = new short[count];
                    Marshal.Copy(data, _pcm, 0, count);
                    for (var i = 0; i < count; i++) _floats[i] = _pcm[i] / 32768f;
                }

                capture.ReleaseBuffer(frames);
                SamplesCaptured?.Invoke(_floats.AsSpan(0, count));
            }
        }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
        try { _client?.Stop(); } catch (COMException) { }
        _client?.Dispose();
        _client = null;
        _ready?.Dispose();
        _ready = null;
    }

    public void Dispose() => Stop();

    /// <summary>Activates an IAudioClient on the virtual process-loopback device, excluding <paramref name="processId"/>'s tree.</summary>
    private static IAudioClient Activate(int processId)
    {
        var parameters = new ActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            LoopbackMode = 1, // PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
        };

        var size = Marshal.SizeOf<ActivationParams>();
        var blob = Marshal.AllocHGlobal(size);
        var variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobPropVariant>());
        try
        {
            Marshal.StructureToPtr(parameters, blob, false);
            Marshal.StructureToPtr(new BlobPropVariant { VarType = 65 /* VT_BLOB */, Size = (uint)size, Data = blob }, variant, false);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualProcessLoopbackDevice, typeof(IAudioClient).GUID, variant, handler, out _);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Process loopback activation timed out.");
            return handler.Result ?? throw new COMException("Process loopback activation failed.", handler.HResult);
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(blob);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int LoopbackMode;
    }

    /// <summary>A PROPVARIANT holding a BLOB (vt, three reserved words, then cbSize and pBlobData).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlobPropVariant
    {
        public ushort VarType;
        public ushort Reserved1, Reserved2, Reserved3;
        public uint Size;
        public IntPtr Data;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateCompletionHandler completionHandler,
        out IActivateOperation operation);

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateCompletionHandler
    {
        void ActivateCompleted(IActivateOperation operation);
    }

    /// <summary>Marks the handler as free-threaded; WASAPI refuses non-agile completion handlers.</summary>
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject;

    private sealed class CompletionHandler : IActivateCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Done { get; } = new();
        public IAudioClient? Result { get; private set; }
        public int HResult { get; private set; }

        public void ActivateCompleted(IActivateOperation operation)
        {
            operation.GetActivateResult(out var hr, out var activated);
            HResult = hr;
            if (hr >= 0) Result = (IAudioClient)activated;
            Done.Set();
        }
    }
}
