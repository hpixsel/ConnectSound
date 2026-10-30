using Android.Content;
using Android.OS;
using AndroidX.Core.Content;
using ConnectSound.Core.Updates;
using Application = Android.App.Application;

namespace ConnectSound.Android;

/// <summary>
/// Looks for a newer APK on GitHub Releases, downloads it and hands it to the system installer.
/// The user confirms the install; Android keeps the app's data because the signing key stays the same.
/// </summary>
internal sealed class ApkUpdater : IUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct)
    {
        var release = await GitHubReleases.GetLatestAsync(Http, ct).ConfigureAwait(false);
        var latest = GitHubReleases.ParseVersion(release?.TagName);
        var current = GitHubReleases.ParseVersion(AndroidPlatform.Instance.AppVersion);
        if (release is null || latest is null || current is null || latest <= current) return null;

        var apk = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        return apk is null ? null : new AvailableUpdate(latest, release.Body, apk.DownloadUrl);
    }

    public async Task ApplyAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken ct)
    {
        var context = Application.Context;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && !context.PackageManager!.CanRequestPackageInstalls())
        {
            // One-time switch the user has to flip; afterwards updates install straight away.
            var settings = new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                global::Android.Net.Uri.Parse($"package:{context.PackageName}"));
            settings.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(settings);
            throw new UpdateBlockedException("Zezwól ConnectSound na instalowanie aplikacji w ustawieniach, które się otworzyły, i stuknij „Zainstaluj teraz” jeszcze raz.");
        }

        var folder = new Java.IO.File(context.CacheDir, "updates");
        folder.Mkdirs();
        var file = new Java.IO.File(folder, "ConnectSound.apk");

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(file.AbsolutePath);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        var uri = FileProvider.GetUriForFile(context, $"{context.PackageName}.updates", file);
        var install = new Intent(Intent.ActionView);
        install.SetDataAndType(uri, "application/vnd.android.package-archive");
        install.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        context.StartActivity(install);
    }
}
