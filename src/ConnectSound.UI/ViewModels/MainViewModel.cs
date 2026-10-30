using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectSound.Core.Net;
using ConnectSound.Core.Platform;

namespace ConnectSound.UI.ViewModels;

/// <summary>Shared services handed to every page.</summary>
public sealed class AppServices(IPlatform platform, AppSettings settings)
{
    public IPlatform Platform { get; } = platform;
    public AppSettings Settings { get; } = settings;

    public DeviceIdentity Identity =>
        new(Settings.DeviceId, string.IsNullOrWhiteSpace(Settings.DeviceName) ? Platform.DefaultDeviceName : Settings.DeviceName!, Platform.Kind);
}

public abstract class PageViewModel : ObservableObject, IAsyncDisposable
{
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty] private PageViewModel _page;

    public MainViewModel(IPlatform platform)
    {
        var settings = AppSettings.Load(platform.DataDirectory);
        _services = new AppServices(platform, settings);
        Update = new UpdateViewModel(platform.Updater, platform.AppVersion);

        _page = settings.Role switch
        {
            AppRole.Host => new HostViewModel(_services),
            AppRole.Source => new SourceViewModel(_services),
            _ => new RoleChooserViewModel(_services, this),
        };
    }

    public UpdateViewModel Update { get; }

    public string DeviceName => _services.Identity.Name;

    public bool IsChoosingRole => Page is RoleChooserViewModel;

    /// <summary>One line for the tray menu and tooltip.</summary>
    public string StatusText => Page switch
    {
        HostViewModel host => host.Summary,
        SourceViewModel { IsStreaming: true } source => $"Wysyłam dźwięk do: {source.HostName}",
        SourceViewModel => "Nie wysyłam dźwięku",
        _ => "Wybierz, co ma robić to urządzenie",
    };

    public string RoleLabel => Page switch
    {
        HostViewModel => "Nadrzędne",
        SourceViewModel => "Źródło",
        _ => "",
    };

    partial void OnPageChanged(PageViewModel? oldValue, PageViewModel newValue)
    {
        if (oldValue is not null) _ = oldValue.DisposeAsync().AsTask();
        OnPropertyChanged(nameof(IsChoosingRole));
        OnPropertyChanged(nameof(RoleLabel));
        OnPropertyChanged(nameof(DeviceName));
    }

    internal void Choose(AppRole role)
    {
        _services.Settings.Role = role;
        _services.Settings.Save();
        Page = role == AppRole.Host ? new HostViewModel(_services) : new SourceViewModel(_services);
    }

    [RelayCommand]
    private void ChangeRole()
    {
        _services.Settings.Role = AppRole.Undecided;
        _services.Settings.Save();
        Page = new RoleChooserViewModel(_services, this);
    }

    public Task ShutdownAsync() => Page.DisposeAsync().AsTask();
}

public sealed partial class RoleChooserViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    [ObservableProperty] private string _deviceName;

    public RoleChooserViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        _deviceName = services.Identity.Name;
    }

    public bool IsPhone => _services.Platform.Kind == DeviceKind.Phone;

    partial void OnDeviceNameChanged(string value)
    {
        _services.Settings.DeviceName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _services.Settings.Save();
    }

    [RelayCommand]
    private void BeHost() => _main.Choose(AppRole.Host);

    [RelayCommand]
    private void BeSource() => _main.Choose(AppRole.Source);
}
