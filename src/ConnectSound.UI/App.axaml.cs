using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ConnectSound.Core.Platform;
using ConnectSound.UI.ViewModels;
using ConnectSound.UI.Views;

namespace ConnectSound.UI;

public partial class App : Application
{
    /// <summary>Set by the platform head (Windows or Android) before Avalonia starts.</summary>
    public static IPlatform Platform { get; set; } = null!;

    /// <summary>Optional hook for the platform head, called once the main window or view exists.</summary>
    public static Action<Avalonia.Controls.TopLevel, MainViewModel>? Started { get; set; }

    public const string DefaultFont = "avares://ConnectSound.UI/Assets/Fonts#Barlow";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var vm = new MainViewModel(Platform);

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow { DataContext = vm };
                desktop.MainWindow.Opened += (_, _) => Started?.Invoke(desktop.MainWindow, vm);
                desktop.ShutdownRequested += (_, _) => _ = vm.ShutdownAsync();
                break;
            case ISingleViewApplicationLifetime single:
                single.MainView = new MainView { DataContext = vm };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
