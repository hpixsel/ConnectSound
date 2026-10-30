using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectSound.Core.Updates;

namespace ConnectSound.UI.ViewModels;

/// <summary>
/// Looks for a new release shortly after start, every few hours, and whenever the user comes back to
/// the app. The process can live for days (tray on Windows, foreground service on Android), so a
/// single check at launch would miss releases published in the meantime.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(10);

    private readonly IUpdater _updater;
    private AvailableUpdate? _update;
    private Version? _dismissed;
    private DateTime _lastCheck = DateTime.MinValue;
    private int _checking;

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _text;

    public UpdateViewModel(IUpdater updater, string currentVersion)
    {
        _updater = updater;
        CurrentVersion = currentVersion;
        _ = RunAsync();
    }

    public string CurrentVersion { get; }

    public string VersionLabel => $"Wersja {CurrentVersion}";

    /// <summary>Call when the app returns to the foreground; checks unless a check ran in the last few minutes.</summary>
    public void CheckSoon()
    {
        if (DateTime.UtcNow - _lastCheck > MinimumGap) _ = CheckAsync();
    }

    private async Task RunAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        while (true)
        {
            await CheckAsync();
            await Task.Delay(Interval);
        }
    }

    private async Task CheckAsync()
    {
        if (IsInstalling || Interlocked.Exchange(ref _checking, 1) == 1) return;
        AvailableUpdate? update;
        try
        {
            _lastCheck = DateTime.UtcNow;
            update = await _updater.CheckAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            return; // offline, GitHub unreachable or rate-limited; the next check will try again
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }

        // "Później" hides this version until a newer one comes out.
        if (update is null || update.Version == _dismissed) return;
        _update = update;
        Text = $"Wersja {update.Version.ToString(3)} jest gotowa";
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
    private void Dismiss()
    {
        _dismissed = _update?.Version;
        IsAvailable = false;
    }
}
