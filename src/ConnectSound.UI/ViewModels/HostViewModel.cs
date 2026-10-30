using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectSound.Core.Audio;
using ConnectSound.Core.Net;

namespace ConnectSound.UI.ViewModels;

/// <summary>This device plays everything. Shows one channel strip per connected device.</summary>
public sealed partial class HostViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly HostSession? _session;
    private readonly IAudioOutput? _output;
    private readonly DispatcherTimer _meters;

    [ObservableProperty] private double _masterGain;
    [ObservableProperty] private double _masterLevel;
    [ObservableProperty] private bool _callActive;
    [ObservableProperty] private string? _callerName;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _outputProblem;

    public HostViewModel(AppServices services)
    {
        _services = services;
        _masterGain = services.Settings.MasterGain;
        Addresses = LocalAddresses();

        try
        {
            _output = services.Platform.CreateOutput();
            _session = new HostSession(services.Identity, _output);
            _session.Mixer.MasterGain = (float)_masterGain;
            _session.SourcesChanged += () => Dispatcher.UIThread.Post(SyncChannels);
            _session.Start();
        }
        catch (SocketException)
        {
            Error = "Nie mogę otworzyć portów 47801–47802. Zamknij drugą kopię ConnectSound albo zezwól aplikacji w zaporze sieciowej.";
        }

        _meters = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick());
        _meters.Start();
        UpdateBackgroundNote();
    }

    public ObservableCollection<ChannelViewModel> Channels { get; } = [];

    public bool IsEmpty => Channels.Count == 0;

    /// <summary>Settable so demo screenshots don't publish the real machine's addresses.</summary>
    [ObservableProperty] public partial string Addresses { get; set; }

    public string Summary => Channels.Count switch
    {
        0 => "Czekam na urządzenia",
        1 => "Gra 1 urządzenie",
        < 5 => $"Grają {Channels.Count} urządzenia",
        _ => $"Gra {Channels.Count} urządzeń",
    };

    public string MasterGainText => Decibels.Format(MasterGain);

    partial void OnMasterGainChanged(double value)
    {
        OnPropertyChanged(nameof(MasterGainText));
        if (_session is not null) _session.Mixer.MasterGain = (float)value;
        _services.Settings.MasterGain = (float)value;
        _services.Settings.SaveSoon();
    }

    private void SyncChannels()
    {
        if (_session is null) return;
        var sources = _session.Sources;

        foreach (var gone in Channels.Where(c => !sources.Contains(c.Source)).ToList())
            Channels.Remove(gone);

        foreach (var source in sources.Where(s => Channels.All(c => c.Source != s)))
            Channels.Add(new ChannelViewModel(source, _session, _services.Settings));

        foreach (var c in Channels) c.Refresh();

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        UpdateBackgroundNote();
    }

    private void Tick()
    {
        if (_session is null) return;
        foreach (var c in Channels) c.Level = c.Source.Channel.Meter.Level;
        MasterLevel = _session.Mixer.MasterMeter.Level;
        OutputProblem = _output?.Problem;

        var caller = Channels.FirstOrDefault(c => c.InCall && !c.Muted);
        CallActive = caller is not null;
        CallerName = caller?.Name;
    }

    private void UpdateBackgroundNote() =>
        _services.Platform.SetBackgroundActivity(Channels.Count == 0 ? "Czekam na urządzenia" : Summary);

    private static string LocalAddresses()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => (Nic: n, a.Address)))
                .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !x.Address.ToString().StartsWith("169.254."))
                .ToList();

            // Virtual adapters (Hyper-V, VPNs) usually have no gateway; the real Wi-Fi/LAN one does.
            var preferred = candidates.Where(x => HasGateway(x.Nic)).ToList();
            var addresses = (preferred.Count > 0 ? preferred : candidates).Select(x => x.Address.ToString()).Distinct().ToList();
            return addresses.Count == 0 ? "brak sieci" : string.Join(", ", addresses);
        }
        catch (NetworkInformationException)
        {
            return "nieznany";
        }
    }

    private static bool HasGateway(NetworkInterface nic)
    {
        try { return nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)); }
        catch (PlatformNotSupportedException) { return true; }
    }

    public override async ValueTask DisposeAsync()
    {
        _meters.Stop();
        _services.Platform.SetBackgroundActivity(null);
        if (_session is not null) await _session.DisposeAsync();
    }
}

public sealed partial class ChannelViewModel : ObservableObject
{
    private readonly HostSession _session;
    private readonly Core.Platform.AppSettings _settings;

    [ObservableProperty] private double _level;
    [ObservableProperty] private double _gain;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private bool _inCall;
    [ObservableProperty] private SourceProfile _profile;

    public ChannelViewModel(RemoteSource source, HostSession session, Core.Platform.AppSettings settings)
    {
        Source = source;
        _session = session;
        _settings = settings;
        _gain = settings.SourceGains.TryGetValue(source.Identity.Id, out var saved) ? saved : 1.0;
        session.SetGain(source, (float)_gain);
        Refresh();
    }

    public RemoteSource Source { get; }

    public string Name => Source.Identity.Name;

    public bool IsPhone => Source.Identity.Kind == DeviceKind.Phone;

    public string KindLabel => IsPhone ? "Telefon" : "Komputer";

    public string ProfileLabel => Profile == SourceProfile.Calls ? "Rozmowy" : "Muzyka";

    public string Address => Source.Address.ToString();

    public void Refresh()
    {
        InCall = Source.InCall;
        Profile = Source.Profile;
        OnPropertyChanged(nameof(ProfileLabel));
    }

    public string GainText => Decibels.Format(Gain);

    partial void OnGainChanged(double value)
    {
        OnPropertyChanged(nameof(GainText));
        _session.SetGain(Source, (float)value);
        _settings.SourceGains[Source.Identity.Id] = (float)value;
        _settings.SaveSoon();
    }

    partial void OnMutedChanged(bool value) => _session.SetMuted(Source, value);

    [RelayCommand]
    private void Disconnect() => _session.Disconnect(Source);
}

internal static class Decibels
{
    /// <summary>Fader position as a console would print it: "0.0 dB" at unity, "−∞" at the bottom.</summary>
    public static string Format(double gain) =>
        gain <= 0.001 ? "−∞ dB" : (20 * Math.Log10(gain)).ToString("+0.0;−0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB";
}
