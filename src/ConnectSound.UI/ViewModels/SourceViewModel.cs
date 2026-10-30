using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectSound.Core.Audio;
using ConnectSound.Core.Net;

namespace ConnectSound.UI.ViewModels;

public enum SourceState { Searching, Connecting, Streaming }

/// <summary>This device sends what it plays to a host.</summary>
public sealed partial class SourceViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly HostBrowser _browser;
    private readonly DispatcherTimer _meter;
    private SourceClient? _client;
    private bool _autoConnectTried;

    [ObservableProperty] private SourceState _state;
    [ObservableProperty] private string? _hostName;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private double _level;
    [ObservableProperty] private bool _inCall;
    [ObservableProperty] private bool _isCallsProfile;
    [ObservableProperty] private string _manualAddress = "";

    public SourceViewModel(AppServices services)
    {
        _services = services;
        _isCallsProfile = services.Settings.Profile == SourceProfile.Calls;

        _browser = new HostBrowser(services.Identity.Id);
        _browser.HostsChanged += () => Dispatcher.UIThread.Post(SyncHosts);
        _browser.Start();
        if (_browser.Error is not null)
            Message = "Nie słyszę sieci: port 47800 jest zajęty. Możesz połączyć się, wpisując adres ręcznie.";

        if (services.Platform.CallMonitor is { } calls)
        {
            calls.InCallChanged += OnCallMonitorChanged;
            calls.Start();
            _inCall = calls.InCall;
        }

        _meter = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
            Level = _client is not null && State == SourceState.Streaming ? _client.Meter.Level : 0);
        _meter.Start();
    }

    public ObservableCollection<DiscoveredHost> Hosts { get; } = [];

    public string VersionLabel => $"Wersja {_services.Platform.AppVersion}";

    public bool HasHosts => Hosts.Count > 0;
    public bool IsSearching => State == SourceState.Searching;
    public bool IsConnecting => State == SourceState.Connecting;
    public bool IsStreaming => State == SourceState.Streaming;
    public bool CanMonitorCalls => _services.Platform.CallMonitor is not null;
    public string? CaptureNote => _services.Platform.CaptureNote;

    public string ProfileHint => IsCallsProfile
        ? "Gdy dzwonisz, muzyka z innych urządzeń przycisza się automatycznie."
        : "Muzyka, filmy, gry. Przycisza się, gdy inny telefon prowadzi rozmowę.";

    partial void OnStateChanged(SourceState value)
    {
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsStreaming));
        _services.Platform.SetBackgroundActivity(value == SourceState.Streaming ? $"Wysyłam dźwięk do: {HostName}" : null);
    }

    public bool IsMediaProfile
    {
        get => !IsCallsProfile;
        set { if (value) IsCallsProfile = false; }
    }

    partial void OnIsCallsProfileChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMediaProfile));
        var profile = value ? SourceProfile.Calls : SourceProfile.Media;
        _services.Settings.Profile = profile;
        _services.Settings.Save();
        OnPropertyChanged(nameof(ProfileHint));
        if (_client is not null) _ = _client.SetProfileAsync(profile);
    }

    private void OnCallMonitorChanged(bool inCall) => Dispatcher.UIThread.Post(() => OnCallChanged(inCall));

    private void OnCallChanged(bool inCall)
    {
        InCall = inCall;
        if (_client is not null) _ = _client.SetInCallAsync(inCall);
    }

    private void SyncHosts()
    {
        var current = _browser.Hosts;
        Hosts.Clear();
        foreach (var h in current) Hosts.Add(h);
        OnPropertyChanged(nameof(HasHosts));

        // On a computer there's no consent dialog, so reconnecting to the last host on its own is safe and expected.
        if (!_autoConnectTried && State == SourceState.Searching && _services.Platform.Kind == DeviceKind.Computer
            && current.FirstOrDefault(h => h.Id == _services.Settings.LastHostId) is { } last)
        {
            _autoConnectTried = true;
            _ = ConnectAsync(last.Control, last.Id);
        }
    }

    [RelayCommand]
    private Task Connect(DiscoveredHost host) => ConnectAsync(host.Control, host.Id);

    [RelayCommand]
    private Task ConnectManual()
    {
        var text = ManualAddress.Trim();
        if (!IPEndPoint.TryParse(text, out var endpoint) || endpoint.AddressFamily != AddressFamily.InterNetwork)
        {
            Message = "Wpisz adres IPv4, np. 192.168.1.20. Znajdziesz go na ekranie urządzenia nadrzędnego.";
            return Task.CompletedTask;
        }
        if (endpoint.Port == 0) endpoint.Port = Ports.Control;
        return ConnectAsync(endpoint, null);
    }

    private async Task ConnectAsync(IPEndPoint endpoint, string? hostId)
    {
        if (State != SourceState.Searching) return;
        State = SourceState.Connecting;
        Message = null;

        var capture = await _services.Platform.CreateCaptureAsync();
        if (capture is null)
        {
            State = SourceState.Searching;
            Message = "Bez zgody na przechwytywanie dźwięku to urządzenie nie ma czego wysłać. Spróbuj jeszcze raz i stuknij „Rozpocznij”.";
            return;
        }

        var client = new SourceClient(_services.Identity, capture);
        try
        {
            var profile = IsCallsProfile ? SourceProfile.Calls : SourceProfile.Media;
            await client.ConnectAsync(endpoint, profile, InCall, CancellationToken.None);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException or HostRejectedException or InvalidDataException or AudioDeviceException)
        {
            await client.DisposeAsync();
            State = SourceState.Searching;
            Message = e is HostRejectedException or AudioDeviceException ? e.Message : $"Nie udało się połączyć z {endpoint.Address}. Sprawdź, czy oba urządzenia są w tej samej sieci Wi-Fi.";
            return;
        }

        _client = client;
        client.Disconnected += reason => Dispatcher.UIThread.Post(() => OnDisconnected(client, reason));
        HostName = client.HostName ?? endpoint.Address.ToString();
        if (hostId is not null)
        {
            _services.Settings.LastHostId = hostId;
            _services.Settings.Save();
        }
        State = SourceState.Streaming;
    }

    private void OnDisconnected(SourceClient client, DisconnectReason reason)
    {
        if (_client != client) return;
        _client = null;
        _ = client.DisposeAsync().AsTask();
        State = SourceState.Searching;
        Message = reason switch
        {
            DisconnectReason.HostStopped => $"{HostName} przestało odtwarzać.",
            DisconnectReason.HostRemovedUs => $"{HostName} odłączyło to urządzenie.",
            DisconnectReason.ConnectionLost => $"Utracono połączenie z {HostName}.",
            _ => null,
        };
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        var client = _client;
        _client = null;
        if (client is not null) await client.DisposeAsync();
        State = SourceState.Searching;
    }

    public override async ValueTask DisposeAsync()
    {
        _meter.Stop();
        if (_services.Platform.CallMonitor is { } calls)
        {
            calls.InCallChanged -= OnCallMonitorChanged;
            calls.Stop();
        }
        _services.Platform.SetBackgroundActivity(null);
        if (_client is not null) await _client.DisposeAsync();
        await _browser.DisposeAsync();
    }
}
