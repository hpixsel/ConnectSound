using Avalonia.Data.Converters;

namespace ConnectSound.UI.ViewModels;

public static class Labels
{
    public static readonly IValueConverter CallLamp =
        new FuncValueConverter<bool, string>(inCall => inCall ? "Rozmowa" : "Bez rozmowy");
}
