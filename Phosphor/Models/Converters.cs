using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Phosphor;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = parameter as string == "Invert";
        bool val = value switch
        {
            bool b => b,
            int i => i > 0,
            _ => false
        };
        if (invert) val = !val;
        return val ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class QueueIndexConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not FrameworkElement container)
            return values.Length >= 3 ? values[2]?.ToString() ?? "" : "";

        string title = values[2]?.ToString() ?? "";

        var itemsControl = System.Windows.Controls.ItemsControl.ItemsControlFromItemContainer(container);
        if (itemsControl == null)
            return title;

        try
        {
            int index = itemsControl.ItemContainerGenerator.IndexFromContainer(container);
            return index >= 0 ? $"{index + 1}. {title}" : title;
        }
        catch (ArgumentException)
        {
            return title;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns true if the bound item is the same object as the CurrentQueueItem.
/// Bind values: [0] = DataContext (the VideoItem), [1] = CurrentQueueItem from ViewModel.
/// </summary>
public class IsCurrentQueueItemConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        return values[0] != null && ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a fractional position (0.0–1.0) and container width to a Canvas.Left value.
/// </summary>
public class FractionToCanvasLeftConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is double fraction && values[1] is double width)
            return fraction * width;
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Turns a PascalCase enum value into spaced, human-friendly text (e.g. "RecentlyAdded" -> "Recently Added").
/// Used to display the Favorites Group/Sort enum lists.
/// </summary>
public class EnumLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null) return "";
        return System.Text.RegularExpressions.Regex.Replace(
            value.ToString() ?? "", "(?<=[a-z0-9])(?=[A-Z])", " ");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
