using System.Net;
using System.Net.Sockets;
using ConnectSound.Core.Audio;

namespace ConnectSound.Core.Net;

public enum DisconnectReason { User, HostStopped, HostRemovedUs, ConnectionLost, VersionMismatch }

/// <summary>
/// A device that sends what it plays to a host. Owns the capture for the lifetime of the connection.
/// </summary>
public sealed class SourceClient : IAsyncDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly IAudioCapture _capture;
    private readonly FrameAccumulator _accumulator = new();
    private readonly OpusFrameEncoder _encoder = new();
    private readonly byte[] _datagram = new byte[AudioPacket.HeaderSize + OpusFrameEncoder.MaxPacketBytes];
    private readonly byte[] _opus = new byte[OpusFrameEncoder.MaxPacketBytes];
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _captureLock = new();

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private UdpClient? _udp;
    private uint _token, _sequence;
    private Task? _readLoop, _pingLoop;
    private SourceProfile _profile;
    private bool _inCall;
    private int _disconnected;

    public SourceClient(DeviceIdentity identity, IAudioCapture capture)
    {
        _identity = identity;
        _capture = capture;
        _accumulator.FrameReady += SendFrame;
        _capture.SamplesCaptured += OnSamples;
    }

    public LevelMeter Meter { get; } = new();

    public string? HostName { get; private set; }

    /// <summary>Raised once, from a background thread, when the session ends for any reason.</summary>
    public event Action<DisconnectReason>? Disconnected;

    public async Task ConnectAsync(IPEndPoint host, SourceProfile profile, bool inCall, CancellationToken ct)
    {
        _profile = profile;
        _inCall = inCall;

        _tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _tcp.ConnectAsync(host, timeout.Token).ConfigureAwait(false);
            _stream = _tcp.GetStream();

            await ControlCodec.WriteAsync(_stream, new ControlMessage
            {
                Type = MessageTypes.Hello,
                App = ControlMessage.AppTag,
                Protocol = ControlMessage.ProtocolVersion,
                DeviceId = _identity.Id,
                Name = _identity.Name,
                Kind = _identity.Kind,
                Profile = profile,
                InCall = inCall,
            }, timeout.Token).ConfigureAwait(false);

            var welcome = await ControlCodec.ReadAsync(_stream, timeout.Token).ConfigureAwait(false);
            if (welcome is { Type: MessageTypes.Bye, Reason: "version" })
                throw new HostRejectedException("Urządzenie nadrzędne ma inną wersję aplikacji. Zaktualizuj obie.");
            if (welcome is not { Type: MessageTypes.Welcome, Token: { } token, AudioPort: { } audioPort })
                throw new HostRejectedException("Urządzenie nie odpowiedziało jak ConnectSound.");

            _token = token;
            HostName = welcome.Name;
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Connect(new IPEndPoint(host.Address, audioPort));
        }

        _readLoop = Task.Run(() => ReadAsync(_cts.Token));
        _pingLoop = Task.Run(() => PingAsync(_cts.Token));
        _capture.Start();
    }

    public Task SetProfileAsync(SourceProfile profile)
    {
        _profile = profile;
        return SendStateAsync();
    }

    public Task SetInCallAsync(bool inCall)
    {
        if (_inCall == inCall) return Task.CompletedTask;
        _inCall = inCall;
        return SendStateAsync();
    }

    private Task SendStateAsync() =>
        SendAsync(new ControlMessage { Type = MessageTypes.State, Profile = _profile, InCall = _inCall });

    private async Task SendAsync(ControlMessage message)
    {
        if (_stream is null) return;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await ControlCodec.WriteAsync(_stream, message, _cts.Token).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
        finally { _writeLock.Release(); }
    }

    private void OnSamples(ReadOnlySpan<float> samples)
    {
        // Capture APIs normally call back on one thread, but timer-driven ones can overlap.
        lock (_captureLock)
        {
            Meter.Feed(samples);
            _accumulator.Push(samples);
        }
    }

    private void SendFrame(float[] frame)
    {
        if (_udp is null) return;
        var length = _encoder.Encode(frame, _opus);
        var size = AudioPacket.Write(_datagram, _token, _sequence++, _opus.AsSpan(0, length));
        try { _udp.Send(_datagram, size); }
        catch (SocketException) { /* transient (Wi-Fi roaming); the control channel decides whether we're still connected */ }
        catch (ObjectDisposedException) { }
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        var reason = DisconnectReason.ConnectionLost;
        try
        {
            while (true)
            {
                var msg = await ControlCodec.ReadAsync(_stream!, ct).ConfigureAwait(false);
                if (msg is null) break;
                if (msg.Type == MessageTypes.Bye)
                {
                    reason = msg.Reason == "host-stopped" ? DisconnectReason.HostStopped : DisconnectReason.HostRemovedUs;
                    break;
                }
            }
        }
        catch (OperationCanceledException) { reason = DisconnectReason.User; }
        catch (Exception e) when (e is IOException or InvalidDataException or SocketException or ObjectDisposedException) { }

        RaiseDisconnected(reason);
    }

    private async Task PingAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await SendAsync(new ControlMessage { Type = MessageTypes.Ping }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private void RaiseDisconnected(DisconnectReason reason)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        _capture.Stop();
        Disconnected?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        _capture.SamplesCaptured -= OnSamples;
        if (Volatile.Read(ref _disconnected) == 0) await SendAsync(new ControlMessage { Type = MessageTypes.Bye }).ConfigureAwait(false);
        RaiseDisconnected(DisconnectReason.User);

        await _cts.CancelAsync();
        foreach (var t in new[] { _readLoop, _pingLoop })
            if (t is not null) await t.ContinueWith(_ => { }, TaskScheduler.Default);

        _udp?.Dispose();
        _tcp?.Dispose();
        _capture.Dispose();
        _cts.Dispose();
    }
}

public sealed class HostRejectedException(string message) : Exception(message);
