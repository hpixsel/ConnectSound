using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using ConnectSound.UI.ViewModels;
using ConnectSound.UI.Views;

namespace ConnectSound.UI;

/// <summary>
/// Desktop behaviour around the main window: closing it hides ConnectSound to the notification area
/// so audio keeps flowing; only "Zamknij ConnectSound" in the tray menu really exits.
/// </summary>
public sealed class DesktopShell
{
    private static DesktopShell? _current;

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainViewModel _vm;
    private readonly MainWindow _window;
    private readonly NativeMenuItem _status;
    private readonly TrayIcon _tray;
    private bool _exiting;

    private DesktopShell(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel vm)
    {
        _desktop = desktop;
        _vm = vm;
        _window = new MainWindow { DataContext = vm };
        _window.Closing += OnClosing;
        _window.Activated += (_, _) => vm.Update.CheckSoon();

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.MainWindow = _window;

        _status = new NativeMenuItem { IsEnabled = false };
        var show = new NativeMenuItem("Pokaż ConnectSound");
        show.Click += (_, _) => Show();
        var quit = new NativeMenuItem("Zamknij ConnectSound");
        quit.Click += (_, _) => Exit();

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ConnectSound.UI/Assets/icon.ico"))),
            Menu = [_status, new NativeMenuItem(vm.Update.VersionLabel) { IsEnabled = false }, new NativeMenuItemSeparator(), show, quit],
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => Show();
        TrayIcon.SetIcons(Application.Current!, [_tray]);

        // Windows is logging off or shutting down: let it, but still say goodbye to connected devices.
        desktop.ShutdownRequested += (_, _) =>
        {
            if (_exiting) return;
            _exiting = true;
            _tray.IsVisible = false;
            _ = vm.ShutdownAsync();
        };

        // The status line in the menu and the tooltip follow what the app is doing.
        DispatcherTimer.Run(() => { RefreshStatus(); return !_exiting; }, TimeSpan.FromSeconds(1));
        RefreshStatus();
    }

    public static MainWindow Attach(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel vm)
    {
        _current = new DesktopShell(desktop, vm);
        return _current._window;
    }

    /// <summary>Brings the window back, e.g. when the user starts ConnectSound again while it sits in the tray.</summary>
    public static void BringToFront() => Dispatcher.UIThread.Post(() => _current?.Show());

    /// <summary>Quits for real, bypassing hide-to-tray (used by demo screenshots and updates).</summary>
    public static void Exit() => Dispatcher.UIThread.Post(() => _ = _current?.QuitAsync());

    private async Task QuitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _tray.IsVisible = false;
        _window.Hide();
        // Give connected devices a moment to hear "bye" so they don't wait for a timeout.
        await Task.WhenAny(_vm.ShutdownAsync(), Task.Delay(1500));
        _desktop.Shutdown();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting || e.CloseReason is WindowCloseReason.OSShutdown) return;
        e.Cancel = true;
        _window.Hide();
    }

    private void Show()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void RefreshStatus()
    {
        var status = _vm.StatusText;
        _status.Header = status;
        _tray.ToolTipText = $"ConnectSound: {status}";
    }
}
