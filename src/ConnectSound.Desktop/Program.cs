using Avalonia;
using Avalonia.Media;
using ConnectSound.UI;
using Velopack;

namespace ConnectSound.Desktop;

internal static class Program
{
    private const string InstanceName = "ConnectSound.SingleInstance";
    private const string ShowSignalName = "ConnectSound.ShowWindow";

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install/update hooks and exits early when Velopack invokes us.
        VelopackApp.Build().Run();

        var demo = DemoOptions.Parse(args);

        // ConnectSound keeps running in the tray, so starting it again should bring that copy back
        // instead of launching a second one that can't open the network ports. Demo runs are independent.
        Mutex? instance = null;
        var first = true;
        if (demo.DataDirectory is null) instance = new Mutex(true, InstanceName, out first);
        using var _ = instance;
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal)) signal.Set();
            return;
        }
        using var showSignal = instance is not null ? new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName) : null;
        if (showSignal is not null)
        {
            new Thread(() => { while (showSignal.WaitOne()) DesktopShell.BringToFront(); })
            { IsBackground = true, Name = "ConnectSound activation" }.Start();
        }

        var platform = new WindowsPlatform(demo.DataDirectory, demo.Enabled);
        demo.PrepareSettings(platform.DataDirectory);

        App.Platform = platform;
        App.Started = demo.Attach;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = App.DefaultFont })
            .LogToTrace();
}
