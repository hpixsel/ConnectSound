using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using Avalonia.Media;
using ConnectSound.UI;

namespace ConnectSound.Android;

[Activity(
    Label = "ConnectSound",
    Theme = "@style/ConnectSoundTheme",
    Icon = "@mipmap/appicon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.User,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).With(new FontManagerOptions { DefaultFamilyName = App.DefaultFont });

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        AndroidPlatform.Instance.Attach(this);
        App.Platform = AndroidPlatform.Instance;
        base.OnCreate(savedInstanceState);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        AndroidPlatform.Instance.OnActivityResult(requestCode, resultCode, data);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        AndroidPlatform.Instance.OnPermissionsResult(requestCode, grantResults);
    }
}
