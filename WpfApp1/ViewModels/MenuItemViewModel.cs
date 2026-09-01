namespace WpfApp1.ViewModels;

public sealed class MenuItemViewModel
{
    public MenuItemViewModel(string label, string glyph, object viewModel)
    {
        Label = label;
        Glyph = glyph;
        ViewModel = viewModel;
    }

    public string Label { get; }

    public string Glyph { get; }

    public object ViewModel { get; }
}
