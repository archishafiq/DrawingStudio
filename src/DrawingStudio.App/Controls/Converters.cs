using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DrawingStudio.App.Controls
{
    /// <summary>Visible when the bound string equals the parameter (e.g. ActiveTool == "Plans").</summary>
    public sealed class EqualsToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.Equals(value as string, parameter as string, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>True when the bound string equals the parameter (used to highlight the active ribbon button).</summary>
    public sealed class EqualsToBool : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.Equals(value as string, parameter as string, StringComparison.Ordinal);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
