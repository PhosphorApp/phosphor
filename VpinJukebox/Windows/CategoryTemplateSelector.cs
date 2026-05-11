using System.Windows;
using System.Windows.Controls;

namespace VpinJukebox;

public class CategoryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CategoryTemplate { get; set; }
    public DataTemplate? SeparatorTemplate { get; set; }
    public DataTemplate? LineBreakTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is Category cat)
        {
            if (cat.IsLineBreak) return LineBreakTemplate;
            if (cat.IsSeparator) return SeparatorTemplate;
        }
        return CategoryTemplate;
    }
}
