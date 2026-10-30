using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ConnectSound.Core.Audio;

namespace ConnectSound.Core.Net;

/// <summary>A device that streams into this host.</summary>
public sealed class RemoteSource
{
    internal RemoteSource(DeviceIdentity identity, uint token, IPAddress address, SourceProfile profile)
    {
        Identity = identity;
        Token = token;
        Address = address;
        Channel = new MixerChannel(identity.Id) { Profile = profile };
    }

    public DeviceIdentity Identity { get; }
    public IPAddress Address { get; }
    public MixerChannel Channel { get; }
    internal uint Token { get; }
    internal OpusFrameDecoder Decoder { get; } = new();
    internal uint? LastSequence { get; set; }
    internal NetworkStream? Stream { get; set; }
    internal CancellationTokenSource Cancel { get; } = new();

    public SourceProfile Profile => Channel.Profile;
    public bool InCall => Channel.InCall;
    public long PacketsLost { get; internal set; }
}

/// <summary>
/// The "master" device: accepts sources, decodes their streams and plays the mix
/// on this device's speakers.
/// </summary>
public sealed class HostSession : IAsyncDisposable
{
    private const int MaxConcealedFrames = 5;
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(8);

    private readonly DeviceIdentity _identity;
    private readonly IAudioOutput _output;
    private readonly ConcurrentDictionary<uint, RemoteSource> _sources = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];
    private TcpListener? _listener;
    private UdpClient? _audio;
    private HostAnnouncer? _announcer;

    /// <param name="controlPort">Pass 0 to let the OS pick (tests); real hosts use the well-known ports so sources can find them.</param>
    public HostSession(DeviceIdentity identity, IAudioOutput output, int controlPort = Ports.Control, int audioPort = Ports.Audio)
    {
        _identity = identity;
        _output = output;
        ControlPort = controlPort;
        AudioPort = audioPort;
    }

    public int ControlPort { get; private set; }
    public int AudioPort { get; private set; }

    public Mixer Mixer { get; } = new();

    public IReadOnlyCollection<RemoteSource> Sources => _sources.Values.OrderBy(s => s.Identity.Name).ToList();

    /// <summary>Raised on a background thread when a source connects, disconnects or changes state.</summary>
    public event Action? SourcesChanged;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, ControlPort);
        _listener.Start();
        ControlPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _audio = new UdpClient(new IPEndPoint(IPAddress.Any, AudioPort));
        AudioPort = ((IPEndPoint)_audio.Client.LocalEndPoint!).Port;
        _audio.Client.ReceiveBufferSize = 1 << 20;

        _loops.Add(Task.Run(() => AcceptAsync(_listener, _cts.Token)));
        _loops.Add(Task.Run(() => ReceiveAudioAsync(_audio, _cts.Token)));

        _output.Start(Mixer);

        _announcer = new HostAnnouncer(_identity, ControlPort);
        _announcer.Start();
    }

    public void SetGain(RemoteSource source, float gain) => source.Channel.Gain = Math.Clamp(gain, 0f, 2f);

    public void SetMuted(RemoteSource source, bool muted) => source.Channel.Muted = muted;

    public void Disconnect(RemoteSource source) => source.Cancel.Cancel();

    private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken hostCt)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        RemoteSource? source = null;

        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var hello = await ControlCodec.ReadAsync(stream, helloTimeout.Token).ConfigureAwait(false);
            if (hello is not { Type: MessageTypes.Hello, App: ControlMessage.AppTag, DeviceId: { } id }) return;

            if (hello.Protocol != ControlMessage.ProtocolVersion)
            {
                await ControlCodec.WriteAsync(stream, new ControlMessage { Type = MessageTypes.Bye, Reason = "version" }, hostCt).ConfigureAwait(false);
                return;
            }

            // A device that reconnects (e.g. after switching Wi-Fi) replaces its old session.
            foreach (var stale in _sources.Values.Where(s => s.Identity.Id == id)) stale.Cancel.Cancel();

            var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.MapToIPv4();
            var identity = new DeviceIdentity(id, hello.Name ?? "Urządzenie", hello.Kind ?? DeviceKind.Phone);
            source = new RemoteSource(identity, NewToken(), address, hello.Profile ?? SourceProfile.Media) { Stream = stream };
            source.Channel.InCall = hello.InCall ?? false;

            _sources[source.Token] = source;
            Mixer.Add(source.Channel);
            SourcesChanged?.Invoke();

            await ControlCodec.WriteAsync(stream, new ControlMessage
            {
                Type = MessageTypes.Welcome,
                App = ControlMessage.AppTag,
                Protocol = ControlMessage.ProtocolVersion,
                DeviceId = _identity.Id,
                Name = _identity.Name,
                Kind = _identity.Kind,
                Token = source.Token,
                AudioPort = AudioPort,
            }, hostCt).ConfigureAwait(false);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostCt, source.Cancel.Token);
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                idle.CancelAfter(ClientTimeout);
                var msg = await ControlCodec.ReadAsync(stream, idle.Token).ConfigureAwait(false);
                if (msg is null || msg.Type == MessageTypes.Bye) break;

                if (msg.Type == MessageTypes.State)
                {
                    if (msg.Profile is { } profile) source.Channel.Profile = profile;
                    if (msg.InCall is { } inCall) source.Channel.InCall = inCall;
                    SourcesChanged?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (InvalidDataException) { }
        catch (SocketException) { }
        finally
        {
            if (source is not null)
            {
                _sources.TryRemove(source.Token, out RemoteSource? _);
                Mixer.Remove(source.Channel);
                SourcesChanged?.Invoke();
                if (!hostCt.IsCancellationRequested)
                {
                    try { await ControlCodec.WriteAsync(stream, new ControlMessage { Type = MessageTypes.Bye }, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
                }
            }
        }
    }

    private async Task ReceiveAudioAsync(UdpClient udp, CancellationToken ct)
    {
        var frame = new float[AudioFormat.FrameSamples];
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; } // ICMP port unreachable etc. on Windows

            if (!AudioPacket.TryRead(result.Buffer, out var token, out var sequence, out var opus)) continue;
            if (!_sources.TryGetValue(token, out var source)) continue;
            if (!result.RemoteEndPoint.Address.MapToIPv4().Equals(source.Address)) continue;

            try { Decode(source, sequence, opus, frame); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { /* corrupt packet */ }
        }
    }

    private static void Decode(RemoteSource source, uint sequence, ReadOnlySpan<byte> opus, float[] frame)
    {
        if (source.LastSequence is { } last)
        {
            var gap = unchecked((int)(sequence - last));
            if (gap <= 0) return; // duplicate or arrived too late to be useful

            if (gap > 1)
            {
                source.PacketsLost += gap - 1;
                if (gap - 1 <= MaxConcealedFrames)
                {
                    for (var i = 1; i < gap; i++)
                    {
                        source.Decoder.Conceal(frame);
                        source.Channel.Buffer.Enqueue(frame);
                    }
                }
                else
                {
                    source.Channel.Buffer.Clear(); // a long dropout: start fresh instead of playing stale audio
                }
            }
        }

        source.LastSequence = sequence;
        source.Decoder.Decode(opus, frame);
        source.Channel.Buffer.Enqueue(frame);
    }

    private uint NewToken()
    {
        uint token;
        do token = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)); while (token == 0 || _sources.ContainsKey(token));
        return token;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sources.Values)
        {
            try
            {
                if (s.Stream is { } stream)
                    await ControlCodec.WriteAsync(stream, new ControlMessage { Type = MessageTypes.Bye, Reason = "host-stopped" }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
        }

        await _cts.CancelAsync();
        if (_announcer is not null) await _announcer.DisposeAsync();
        _output.Stop();
        _listener?.Stop();
        _audio?.Dispose();
        await Task.WhenAll(_loops.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        _cts.Dispose();
    }
}
