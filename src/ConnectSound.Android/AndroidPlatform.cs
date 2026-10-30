using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media.Projection;
using Android.Net.Wifi;
using Android.OS;
using ConnectSound.Core.Audio;
using ConnectSound.Core.Net;
using ConnectSound.Core.Platform;
using ConnectSound.Core.Updates;
using Application = Android.App.Application;

namespace ConnectSound.Android;

internal sealed class AndroidPlatform : IPlatform
{
    private const int PermissionRequest = 41;
    private const int ProjectionRequest = 42;

    public static AndroidPlatform Instance { get; } = new();

    private Activity? _activity;
    private TaskCompletionSource<bool>? _permissions;
    private TaskCompletionSource<Intent?>? _projection;
    private WifiManager.MulticastLock? _multicast;
    private WifiManager.WifiLock? _wifi;

    private AndroidPlatform() { }

    private static Context Context => Application.Context;

    public void Attach(Activity activity)
    {
        _activity = activity;
        if (_multicast is not null) return;

        // Without these, many phones silently drop broadcast discovery packets and throttle Wi-Fi when the screen is off.
        var wifi = (WifiManager)Context.GetSystemService(Context.WifiService)!;
        _multicast = wifi.CreateMulticastLock("connectsound-discovery")!;
        _multicast.SetReferenceCounted(false);
        _multicast.Acquire();
#pragma warning disable CA1422 // WifiMode.FullLowLatency replaces FullHighPerf on API 29+, which is our minimum
        _wifi = wifi.CreateWifiLock(global::Android.Net.WifiMode.FullLowLatency, "connectsound-stream");
#pragma warning restore CA1422
        _wifi!.SetReferenceCounted(false);
    }

    public DeviceKind Kind => DeviceKind.Phone;

    public string DefaultDeviceName =>
        global::Android.Provider.Settings.Global.GetString(Context.ContentResolver, "device_name")
        ?? $"{Build.Manufacturer} {Build.Model}".Trim();

    public string DataDirectory => Context.FilesDir!.AbsolutePath;

    public string AppVersion =>
        (OperatingSystem.IsAndroidVersionAtLeast(33)
            ? Context.PackageManager!.GetPackageInfo(Context.PackageName!, PackageManager.PackageInfoFlags.Of(0L))
#pragma warning disable CA1422 // the int overload is the only one before API 33
            : Context.PackageManager!.GetPackageInfo(Context.PackageName!, 0))?.VersionName ?? "0.0.0";
#pragma warning restore CA1422

    public ICallMonitor? CallMonitor { get; } = new AudioModeCallMonitor();

    public IUpdater Updater { get; } = new ApkUpdater();

    public string? CaptureNote =>
        "Android przekazuje dźwięk aplikacji, które na to pozwalają: Spotify, YouTube i większość odtwarzaczy. " +
        "Samego dźwięku rozmów telefonicznych system nie udostępnia żadnej aplikacji, więc rozmowę słyszysz w telefonie; " +
        "ConnectSound w tym czasie przycisza muzykę z pozostałych urządzeń.";

    public IAudioOutput CreateOutput() => new AudioTrackOutput();

    public async Task<IAudioCapture?> CreateCaptureAsync()
    {
        var activity = _activity ?? throw new InvalidOperationException("No activity attached.");

        if (!await RequestPermissionsAsync(activity)) return null;

        var manager = (MediaProjectionManager)activity.GetSystemService(Context.MediaProjectionService)!;
        _projection = new TaskCompletionSource<Intent?>();
        // On Android 14+ the dialog defaults to "a single app", which would limit capture to that one app.
        var request = OperatingSystem.IsAndroidVersionAtLeast(34)
            ? manager.CreateScreenCaptureIntent(MediaProjectionConfig.CreateConfigForDefaultDisplay())
            : manager.CreateScreenCaptureIntent();
        activity.StartActivityForResult(request, ProjectionRequest);
        var consent = await _projection.Task;
        if (consent is null) return null;

        // Android 14 requires the media-projection foreground service to be running before the projection is created.
        await AudioService.StartAsync(Context, capturing: true, "Przygotowuję przesyłanie dźwięku");
        var projection = manager.GetMediaProjection((int)Result.Ok, consent);
        if (projection is null) return null;

        _wifi?.Acquire();
        return new PlaybackCapture(projection);
    }

    public void SetBackgroundActivity(string? description)
    {
        if (description is null)
        {
            _wifi?.Release();
            AudioService.Stop(Context);
            return;
        }

        _wifi?.Acquire();
        _ = AudioService.StartAsync(Context, capturing: AudioService.IsCapturing, description);
    }

    private async Task<bool> RequestPermissionsAsync(Activity activity)
    {
        var needed = new List<string> { Manifest.Permission.RecordAudio };
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) needed.Add(Manifest.Permission.PostNotifications);

        var missing = needed.Where(p => activity.CheckSelfPermission(p) != Permission.Granted).ToArray();
        if (missing.Length == 0) return true;

        _permissions = new TaskCompletionSource<bool>();
        activity.RequestPermissions(missing, PermissionRequest);
        await _permissions.Task;

        // Notifications are nice to have; recording is what we can't work without.
        return activity.CheckSelfPermission(Manifest.Permission.RecordAudio) == Permission.Granted;
    }

    public void OnPermissionsResult(int requestCode, Permission[] results)
    {
        if (requestCode == PermissionRequest) _permissions?.TrySetResult(results.All(r => r == Permission.Granted));
    }

    public void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode == ProjectionRequest) _projection?.TrySetResult(resultCode == Result.Ok ? data : null);
    }
}
