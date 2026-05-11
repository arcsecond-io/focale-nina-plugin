using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Focale.NINA.Plugin.Ui;

/// <summary>true → Collapsed, false → Visible. Mirror of WPF's built-in BooleanToVisibilityConverter.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter {
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Collapsed;
}
