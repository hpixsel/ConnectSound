using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectSound.Core.Updates;

namespace ConnectSound.UI.ViewModels;

public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdater _updater;
    private AvailableUpdate? _update;

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _text;

    public UpdateViewModel(IUpdater updater, string currentVersion)
    {
        _updater = updater;
        CurrentVersion = currentVersion;
        _ = CheckAsync();
    }

    public string CurrentVersion { get; }

    private async Task CheckAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        try
        {
            _update = await _updater.CheckAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return; // offline or GitHub unreachable; we'll try again next launch
        }

        if (_update is null) return;
        Text = $"Wersja {_update.Version.ToString(3)} jest gotowa";
        IsAvailable = true;
    }

    [RelayCommand]
    private async Task Install()
    {
        if (_update is null || IsInstalling) return;
        IsInstalling = true;
        Text = "Pobieram aktualizację…";
        try
        {
            await _updater.ApplyAsync(_update, new Progress<double>(p => Progress = p), CancellationToken.None);
        }
        catch (UpdateBlockedException e)
        {
            Text = e.Message;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Text = "Pobieranie przerwane. Sprawdź internet i spróbuj ponownie.";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private void Dismiss() => IsAvailable = false;
}
