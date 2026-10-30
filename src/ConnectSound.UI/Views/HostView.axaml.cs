using Avalonia.Controls;

namespace ConnectSound.UI.Views;

public partial class HostView : UserControl
{
    public HostView() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var compact = e.NewSize.Width < 640;
        Wide.IsVisible = !compact;
        Compact.IsVisible = compact;
    }
}
