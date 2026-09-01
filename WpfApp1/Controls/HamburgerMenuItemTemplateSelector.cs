using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;

namespace WpfApp1.Controls;

public sealed class HamburgerMenuItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }

    public DataTemplate? MenuItemTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item is HamburgerMenuHeaderItem ? HeaderTemplate : MenuItemTemplate;
    }
}
