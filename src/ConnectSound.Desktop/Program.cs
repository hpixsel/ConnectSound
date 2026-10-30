using Avalonia;
using Avalonia.Media;
using ConnectSound.UI;
using Velopack;

namespace ConnectSound.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install/update hooks and exits early when Velopack invokes us.
        VelopackApp.Build().Run();

        var demo = DemoOptions.Parse(args);
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
