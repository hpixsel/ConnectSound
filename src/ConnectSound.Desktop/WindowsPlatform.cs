using System.Reflection;
using ConnectSound.Core.Audio;
using ConnectSound.Core.Net;
using ConnectSound.Core.Platform;
using ConnectSound.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace ConnectSound.Desktop;

internal sealed class WindowsPlatform(string? dataDirectory = null, bool silent = false) : IPlatform
{
    public DeviceKind Kind => DeviceKind.Computer;

    public string DefaultDeviceName => Environment.MachineName;

    public string DataDirectory { get; } = dataDirectory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectSound");

    public string AppVersion { get; } =
        typeof(WindowsPlatform).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public Task<IAudioCapture?> CreateCaptureAsync() => Task.FromResult<IAudioCapture?>(new LoopbackCapture());

    public IAudioOutput CreateOutput() => silent ? new DemoOptions.SilentOutput() : new WasapiOutput();

    public ICallMonitor? CallMonitor => null;

    public IUpdater Updater { get; } = new VelopackUpdater();

    public void SetBackgroundActivity(string? description) { }

    public string? CaptureNote => null;
}

/// <summary>Velopack checks GitHub Releases, downloads a delta package and restarts into the new version.</summary>
internal sealed class VelopackUpdater : IUpdater
{
    private readonly UpdateManager _manager = new(new GithubSource(GitHubReleases.RepositoryUrl, null, false));

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct)
    {
        if (!_manager.IsInstalled) return null; // running from a dev build, nothing to update
        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null) return null;

        var version = GitHubReleases.ParseVersion(info.TargetFullRelease.Version.ToString()) ?? new Version(0, 0, 0);
        return new AvailableUpdate(version, info.TargetFullRelease.NotesMarkdown, null, info);
    }

    public async Task ApplyAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken ct)
    {
        var info = (UpdateInfo)update.PlatformState!;
        await _manager.DownloadUpdatesAsync(info, p => progress?.Report(p / 100.0), ct).ConfigureAwait(false);
        _manager.ApplyUpdatesAndRestart(info);
    }
}
