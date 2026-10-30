using ConnectSound.Core.Audio;
using ConnectSound.Core.Net;
using ConnectSound.Core.Updates;

namespace ConnectSound.Core.Platform;

/// <summary>Everything the shared app needs from Windows or Android.</summary>
public interface IPlatform
{
    DeviceKind Kind { get; }
    string DefaultDeviceName { get; }
    string DataDirectory { get; }
    string AppVersion { get; }

    /// <summary>
    /// Asks for whatever the OS requires (on Android: microphone permission and the screen-capture consent dialog)
    /// and returns a capture of everything this device plays, or null when the user declined.
    /// </summary>
    Task<IAudioCapture?> CreateCaptureAsync();

    IAudioOutput CreateOutput();

    /// <summary>Null on platforms where calls can't be observed.</summary>
    ICallMonitor? CallMonitor { get; }

    IUpdater Updater { get; }

    /// <summary>Keeps the process alive and audio flowing while the screen is off (Android foreground service).</summary>
    void SetBackgroundActivity(string? description);

    /// <summary>Things the user should know about this platform's limits, shown once in the UI. Null when none.</summary>
    string? CaptureNote { get; }
}

public interface ICallMonitor
{
    bool InCall { get; }

    /// <summary>May be raised from any thread.</summary>
    event Action<bool>? InCallChanged;

    void Start();
    void Stop();
}
