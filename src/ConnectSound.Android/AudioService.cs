using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace ConnectSound.Android;

/// <summary>
/// Foreground service that keeps streaming alive with the screen off. When capturing it also
/// carries the media-projection type, which Android requires for playback capture.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback | ForegroundService.TypeMediaProjection)]
internal sealed class AudioService : Service
{
    private const string ChannelId = "streaming";
    private const int NotificationId = 1;

    private static TaskCompletionSource? _started;

    public static bool IsCapturing { get; private set; }

    public static async Task StartAsync(Context context, bool capturing, string text)
    {
        IsCapturing = capturing;
        _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = new Intent(context, typeof(AudioService)).PutExtra("text", text).PutExtra("capturing", capturing);
        context.StartForegroundService(intent);
        try { await _started.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { /* the projection call will fail loudly if this really didn't start */ }
    }

    public static void Stop(Context context)
    {
        IsCapturing = false;
        context.StopService(new Intent(context, typeof(AudioService)));
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var text = intent?.GetStringExtra("text") ?? "ConnectSound działa";
        var capturing = intent?.GetBooleanExtra("capturing", false) ?? false;

        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Przesyłanie dźwięku", NotificationImportance.Low)
        {
            Description = "Widoczne, gdy ConnectSound gra lub wysyła dźwięk.",
        });

        var open = PendingIntent.GetActivity(this, 0,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle("ConnectSound")!
            .SetContentText(text)!
            .SetSmallIcon(Resource.Drawable.ic_notification)!
            .SetContentIntent(open)!
            .SetOngoing(true)!
            .SetCategory(Notification.CategoryTransport)!
            .Build()!;

        var type = capturing
            ? ForegroundService.TypeMediaProjection | ForegroundService.TypeMediaPlayback
            : ForegroundService.TypeMediaPlayback;
        StartForeground(NotificationId, notification, type);

        _started?.TrySetResult();
        return StartCommandResult.NotSticky;
    }
}
