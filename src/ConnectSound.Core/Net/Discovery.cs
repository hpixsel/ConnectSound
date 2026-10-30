using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ConnectSound.Core.Net;

public sealed record DeviceIdentity(string Id, string Name, DeviceKind Kind);

public sealed record DiscoveredHost(string Id, string Name, DeviceKind Kind, IPEndPoint Control, DateTimeOffset LastSeen);

/// <summary>Host side: broadcasts its presence on every IPv4 network once a second.</summary>
public sealed class HostAnnouncer : IAsyncDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly int _controlPort;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public HostAnnouncer(DeviceIdentity identity, int controlPort)
    {
        _identity = identity;
        _controlPort = controlPort;
    }

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var payload = ControlCodec.Serialize(new ControlMessage
        {
            Type = MessageTypes.Announce,
            App = ControlMessage.AppTag,
            Protocol = ControlMessage.ProtocolVersion,
            DeviceId = _identity.Id,
            Name = _identity.Name,
            Kind = _identity.Kind,
            ControlPort = _controlPort,
        });

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            foreach (var address in BroadcastAddresses())
            {
                try { await udp.SendAsync(payload, new IPEndPoint(address, Ports.Discovery), ct).ConfigureAwait(false); }
                catch (SocketException) { /* interface went away between enumeration and send; next tick retries */ }
            }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    internal static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var result = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;
                    var ip = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 0)) continue;
                    for (var i = 0; i < 4; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                    result.Add(new IPAddress(ip));
                }
            }
        }
        catch (NetworkInformationException) { /* limited broadcast still works */ }
        catch (PlatformNotSupportedException) { }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null) await _loop.ContinueWith(_ => { }, TaskScheduler.Default);
        _cts.Dispose();
    }
}

/// <summary>Source side: listens for host announcements and keeps a list of hosts seen in the last few seconds.</summary>
public sealed class HostBrowser : IAsyncDisposable
{
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(4);

    private readonly string _ownId;
    private readonly ConcurrentDictionary<string, DiscoveredHost> _hosts = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _receive, _sweep;

    public HostBrowser(string ownId) => _ownId = ownId;

    /// <summary>Raised from a background thread whenever a host appears, changes or disappears.</summary>
    public event Action? HostsChanged;

    public IReadOnlyList<DiscoveredHost> Hosts => _hosts.Values.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Set when the discovery port could not be opened (usually another copy of the app is running).</summary>
    public string? Error { get; private set; }

    public void Start()
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Ports.Discovery));
        }
        catch (SocketException ex)
        {
            Error = ex.Message;
            return;
        }

        _receive = Task.Run(() => ReceiveAsync(udp, _cts.Token));
        _sweep = Task.Run(() => SweepAsync(_cts.Token));
    }

    private async Task ReceiveAsync(UdpClient udp, CancellationToken ct)
    {
        using var _ = udp;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            var msg = ControlCodec.Deserialize(result.Buffer);
            if (msg is not { Type: MessageTypes.Announce, App: ControlMessage.AppTag, DeviceId: { } id, ControlPort: { } port }) continue;
            if (id == _ownId || msg.Protocol != ControlMessage.ProtocolVersion) continue;

            var host = new DiscoveredHost(id, msg.Name ?? "?", msg.Kind ?? DeviceKind.Computer,
                new IPEndPoint(result.RemoteEndPoint.Address, port), DateTimeOffset.UtcNow);

            var isNew = !_hosts.TryGetValue(id, out var previous) || previous.Name != host.Name || !previous.Control.Equals(host.Control);
            _hosts[id] = host;
            if (isNew) HostsChanged?.Invoke();
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var removed = false;
                foreach (var (id, host) in _hosts)
                    if (DateTimeOffset.UtcNow - host.LastSeen > Expiry) removed |= _hosts.TryRemove(id, out _);
                if (removed) HostsChanged?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        foreach (var t in new[] { _receive, _sweep })
            if (t is not null) await t.ContinueWith(_ => { }, TaskScheduler.Default);
        _cts.Dispose();
    }
}
