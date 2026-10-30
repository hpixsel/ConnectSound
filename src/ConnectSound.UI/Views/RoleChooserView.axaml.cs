using Avalonia;
using Avalonia.Controls;

namespace ConnectSound.UI.Views;

public partial class RoleChooserView : UserControl
{
    public RoleChooserView() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var stacked = e.NewSize.Width < 640;
        Choices.Columns = stacked ? 1 : 2;
        Choices.Children[0].Margin = stacked ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 8, 12);
        Choices.Children[1].Margin = stacked ? new Thickness(0, 0, 0, 12) : new Thickness(8, 0, 0, 12);
    }
}
